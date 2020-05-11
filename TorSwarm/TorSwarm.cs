﻿using System;
using System.IO;
using System.Threading;
using System.Security.Cryptography;
using System.Collections.Generic;

using static SuRGeoNix.TorSwarm.Torrent.TorrentData;
using static SuRGeoNix.TorSwarm.Torrent.MetaData;

namespace SuRGeoNix.TorSwarm
{
    public class TorSwarm
    {
        // ====================== PUBLIC ======================

        public bool             isRunning                   { get { return status == Status.RUNNING; } }
        public bool             isPaused                    { get { return status == Status.PAUSED; } }
        public bool             isStopped                   { get { return status == Status.STOPPED; } }

        public OptionsStruct    Options;
        public StatsStructure   Stats;

        public struct OptionsStruct
        {
            public string   DownloadPath        { get; set; }
            public string   TempPath            { get; set; }

            public int      MaxConnections      { get; set; }
            public int      MinThreads          { get; set; }
            public int      MaxThreads          { get; set; }
            public int      PeersFromTracker    { get; set; }

            public int      DownloadLimit       { get; set; }
            public int      UploadLimit         { get; set; }

            public int      ConnectionTimeout   { get; set; }
            public int      HandshakeTimeout    { get; set; }
            public int      MetadataTimeout     { get; set; }
            public int      PieceTimeout        { get; set; }

            public bool     EnableDHT           { get; set; }
            public int      RequestBlocksPerPeer{ get; set; }

            public int      Verbosity           { get; set; }   // 1 -> TorSwarm, 2 -> Peers | Trackers
            public bool     LogTracker          { get; set; }
            public bool     LogPeer             { get; set; }
            public bool     LogDHT              { get; set; }
            public bool     LogStats            { get; set; }

            public Action<StatsStructure>       StatsCallback       { get; set; }
            public Action<Torrent>              TorrentCallback     { get; set; }
            public Action<int, string>          StatusCallback      { get; set; }
        }
        public struct StatsStructure
        {
            public int      DownRate            { get; set; }
            public int      AvgRate             { get; set; }
            public int      MaxRate             { get; set; }

            public int      AvgETA              { get; set; }
            public int      ETA                 { get; set; }

            public long     BytesDownloaded     { get; set; }
            public long     BytesDownloadedPrev { get; set; }
            public long     BytesUploaded       { get; set; }
            public long     BytesDropped        { get; set; }

            public int      PeersTotal          { get; set; }
            public int      PeersInQueue        { get; set; }
            public int      PeersConnecting     { get; set; }
            public int      PeersConnected      { get; set; }
            public int      PeersFailed1        { get; set; }
            public int      PeersFailed2        { get; set; }
            public int      PeersFailed         { get; set; }
            public int      PeersChoked         { get; set; }
            public int      PeersUnChoked       { get; set; }
            public int      PeersDownloading    { get; set; }
            public int      PeersDropped        { get; set; }
        }

        // ====================== PRIVATE =====================

        // Lockers
        private static readonly object  lockerTorrent       = new object();
        private static readonly object  lockerMetadata      = new object();
        private static readonly object  lockerPeers         = new object();

        // Generators (Hash / Random)
        public  static SHA1             sha1                = new SHA1Managed();
        private static Random           rnd                 = new Random();
        private byte[]                  peerID;

        // Main [Torrent / Trackers / Peers / Options]
        private Torrent                 torrent;
        private List<Tracker>           trackers;
        private Tracker.Options         trackerOpt;
        private List<Peer>              peers;
        private Peer.Options            peerOpt;
        private DHT                     dht;
        private DHT.Options             dhtOpt;

        private Logger                  log;
        private Logger                  logDHT;
        private Thread                  beggar;
        private Status                  status;

        private long                    metadataLastRequested;

        // More Stats
        private int                     openConnections     = 0;
        private long                    curSecondTicks      = 0;
        private int                     curSeconds          = 0;
        private int                     pieceTimeouts       = 0;
        private int                     pieceRejected       = 0;
        private int                     pieceAlreadyRecv    = 0;
        private int                     sha1Fails           = 0;
        private int                     dhtPeers            = 0;

        private enum Status
        {
            RUNNING     = 0,
            PAUSED      = 1,
            STOPPED     = 2
        }

        // ================= PUBLIC METHODS ===================

        // Constructors
        public TorSwarm(string torrent, OptionsStruct? opt = null)
        { 
            Options         = (opt == null) ? GetDefaultsOptions() : (OptionsStruct) opt;
            Initiliaze();
            this.torrent.FillFromTorrentFile(torrent);
            Setup();
        }
        public TorSwarm(Uri magnetLink, OptionsStruct? opt = null)
        {
            Options         = (opt == null) ? GetDefaultsOptions() : (OptionsStruct) opt;
            Initiliaze();
            torrent.FillFromMagnetLink(magnetLink);
            Setup();
        }

        // Initializers / Setup
        private void Initiliaze()
        {
            peerID                          = new byte[20]; rnd.NextBytes(peerID);

            trackers                        = new List<Tracker>();
            peers                           = new List<Peer>();

            torrent                         = new Torrent(Options.DownloadPath);
            torrent.metadata.progress       = new BitField(20); // Max Metadata Pieces
            torrent.metadata.pieces         = 2;                // Consider 2 Pieces Until The First Response
            torrent.metadata.parallelRequests= 4;               // How Many Peers We Will Ask In Parallel (firstPieceTries/2)

            log                             = new Logger(Path.Combine(Options.DownloadPath, "session" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log"), true);
            if ( Options.EnableDHT )
                logDHT                      = new Logger(Path.Combine(Options.DownloadPath, "session" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_DHT.log"), true);

            status                          = Status.STOPPED;
        }
        private void Setup()
        {
            // DHT
            if ( Options.EnableDHT )
            {
                dhtOpt                      = DHT.GetDefaultOptions();
                dhtOpt.LogFile              = logDHT;
                dhtOpt.Verbosity            = Options.LogDHT ? Options.Verbosity : 0;
                dhtOpt.NewPeersClbk         = FillPeersFromDHT;
                dht                         = new DHT(torrent.file.infoHash, dhtOpt);
            }

            // Tracker
            trackerOpt                      = new Tracker.Options();
            trackerOpt.PeerId               = peerID;
            trackerOpt.InfoHash             = torrent.file.infoHash;
            trackerOpt.ConnectTimeout       = Options.ConnectionTimeout;
            trackerOpt.ReceiveTimeout       = Options.HandshakeTimeout;

            trackerOpt.LogFile              = log;
            trackerOpt.Verbosity            = Options.LogTracker ? Options.Verbosity : 0;

            // Peer
            peerOpt                         = new Peer.Options();
            peerOpt.PeerID                  = peerID;
            peerOpt.InfoHash                = torrent.file.infoHash;
            peerOpt.ConnectionTimeout       = Options.ConnectionTimeout;
            peerOpt.HandshakeTimeout        = Options.HandshakeTimeout;
            peerOpt.Pieces                  = torrent.data.pieces;

            peerOpt.LogFile                 = log;
            peerOpt.Verbosity               = Options.LogPeer ? Options.Verbosity : 0;

            peerOpt.MetadataReceivedClbk    = MetadataReceived;
            peerOpt.MetadataRejectedClbk    = MetadataRejected;
            peerOpt.PieceReceivedClbk       = PieceReceived;
            peerOpt.PieceRejectedClbk       = PieceRejected;

            // Misc
            ThreadPool.SetMinThreads        (Options.MinThreads, Options.MinThreads);

            FillTrackersFromTorrent();

            if (  (torrent.file.length > 0      || (torrent.file.lengths != null && torrent.file.lengths.Count > 0))  
                && torrent.file.pieceLength > 0 && (torrent.file.pieces  != null && torrent.file.pieces.Count  > 0))
                { torrent.metadata.isDone = true;  Options.TorrentCallback?.BeginInvoke(torrent, null, null); }

            // TODO: 
            // 1. Default Trackers List
            // 2. Local ISP SRV _bittorrent-tracker.<> http://bittorrent.org/beps/bep_0022.html
        }
        public static OptionsStruct GetDefaultsOptions()
        {
            OptionsStruct opt       = new OptionsStruct();

            opt.DownloadPath        = Path.GetTempPath();
            opt.TempPath            = Path.GetTempPath();

            opt.MaxConnections      =  60;
            opt.MinThreads          = 220;
            opt.MaxThreads          = Timeout.Infinite;
            opt.PeersFromTracker    = - 1;

            opt.DownloadLimit       = Timeout.Infinite;
            opt.UploadLimit         = Timeout.Infinite;

            opt.ConnectionTimeout   = 2500;
            opt.HandshakeTimeout    = 3000;
            opt.MetadataTimeout     = 1600;
            opt.PieceTimeout        = 8000;

            opt.EnableDHT           = true;
            opt.RequestBlocksPerPeer=   6;

            opt.Verbosity           =   0;

            opt.LogTracker          = false;
            opt.LogPeer             = false;
            opt.LogDHT              = false;
            opt.LogStats            = false;

            return opt;
        }

        // Start / Pause / Stop
        public void Start()
        {
            if ( status == Status.RUNNING ) return;

            status = Status.RUNNING;

            beggar = new Thread(() =>
            {
                Beggar();

                FillStats();
                if (torrent.data.isDone)
                    Options.StatusCallback?.BeginInvoke(0, "", null, null);
                else
                    Options.StatusCallback?.BeginInvoke(1, "", null, null);
            });

            beggar.SetApartmentState(ApartmentState.STA);
            beggar.Priority = ThreadPriority.AboveNormal;
            beggar.Start();
        }
        public void Pause()
        {
            if ( status == Status.PAUSED ) return;

            status = Status.PAUSED;
        }
        public void Stop()
        {
            if ( status == Status.STOPPED ) return;

            status = Status.STOPPED;
            Thread.Sleep(800);
            
            if ( beggar != null ) beggar.Abort();
        }

        // ================ PRIVATE METHODS =================

        // Feeders              [Torrent -> Trackers | Trackers -> Peers | Client -> Stats]
        private void FillTrackersFromTorrent()
        {
            foreach (Uri uri in torrent.file.trackers)
            {
                if ( uri.Scheme.ToLower() == "http" || uri.Scheme.ToLower() == "https" || uri.Scheme.ToLower() == "udp" )
                {
                    Log($"[Torrent] [Tracker] [ADD] {uri}");
                    trackers.Add(new Tracker(uri, trackerOpt));
                }
                else
                    Log($"[Torrent] [Tracker] [ADD] {uri} Protocol not implemented");
            }
        }
        private void FillPeersFromTracker(int pos)
        {
            if ( !trackers[pos].Announce(Options.PeersFromTracker) ) { if ( Options.LogTracker) trackers[pos].Log($"[{trackers[pos].host}:{trackers[pos].port} Failed"); return; }
            if ( trackers[pos].peers == null) {trackers[pos].Log($"[{trackers[pos].host}:{trackers[pos].port}] No peers"); return; }

            lock ( lockerPeers )
            {
                if ( Options.LogTracker) trackers[pos].Log($"[{trackers[pos].host}:{trackers[pos].port}] [BEFORE] Adding {trackers[pos].peers.Count} in Peers {peers.Count}");
                foreach (Peer peer in peers)
                    if ( trackers[pos].peers.ContainsKey(peer.host) ) { if ( Options.LogTracker) trackers[pos].Log($"[{trackers[pos].host}:{trackers[pos].port}] Peer {peer.host} already exists"); trackers[pos].peers.Remove(peer.host); }

                foreach (KeyValuePair<string, int> peerKV in trackers[pos].peers)
                    peers.Add(new Peer(peerKV.Key, peerKV.Value, peerOpt));

                if ( Options.LogTracker) trackers[pos].Log($"[{trackers[pos].host}:{trackers[pos].port}] [AFTER] Peers {peers.Count}");
            }
        }
        private void FillPeersFromDHT(Dictionary<string, int> newPeers)
        {   
            lock ( lockerPeers )
            {
                if ( Options.LogDHT) dht.Log($"[BEFORE] Adding {newPeers.Count} in Peers {peers.Count}");

                foreach (Peer peer in peers)
                    if ( newPeers.ContainsKey(peer.host) ) { if ( Options.LogDHT) dht.Log($"Peer {peer.host} already exists"); newPeers.Remove(peer.host); }

                foreach (KeyValuePair<string, int> peerKV in newPeers)
                    peers.Add(new Peer(peerKV.Key, peerKV.Value, peerOpt));    

                dhtPeers += newPeers.Count;
                if ( Options.LogDHT) dht.Log($"[AFTER] Peers {peers.Count}");
            }
        }
        private void FillStats()
        {
            List<string> cleanPeers = new List<string>();

            Stats.PeersInQueue      = 0;
            Stats.PeersChoked       = 0;
            Stats.PeersConnected    = 0;
            Stats.PeersConnecting   = 0;
            Stats.PeersDownloading  = 0;
            Stats.PeersDropped      = 0;
            Stats.PeersFailed1      = 0;
            Stats.PeersFailed2      = 0;
            Stats.PeersUnChoked     = 0;
                            
            foreach (Peer peer in peers)
            {
                switch ( peer.status )
                {
                    case Peer.Status.NEW:
                        Stats.PeersInQueue++;

                        break;
                    case Peer.Status.CONNECTING:
                    case Peer.Status.CONNECTED:
                        Stats.PeersConnecting++;

                        break;
                    case Peer.Status.FAILED1:
                        Stats.PeersFailed1++;
                        cleanPeers.Add(peer.host);

                        break;
                    case Peer.Status.FAILED2:
                        Stats.PeersFailed2++;
                        cleanPeers.Add(peer.host);

                        break;
                    case Peer.Status.READY:
                        Stats.PeersConnected++;
                        if ( peer.stageYou.unchoked ) 
                            Stats.PeersUnChoked++;
                        else
                            Stats.PeersChoked++;

                        break;
                    case Peer.Status.DOWNLOADING:
                        Stats.PeersDownloading++;

                        break;
                }
            }

            // Remove FAILED Peers (NOTE: Trackers possible will add them back! Maybe ban them. Also make sure Dispose them!)
            for (int i = peers.Count - 1; i >= 0; i--)
            {
                for (int k = 0; k < cleanPeers.Count; k++)
                    if (peers[i].host == cleanPeers[k]) { cleanPeers.RemoveAt(k); peers[i].Disconnect(); peers.RemoveAt(i); Stats.PeersDropped++; }
            }

            long totalBytesDownloaded = Stats.BytesDownloaded + Stats.BytesDropped;
            Stats.DownRate      = (int) (totalBytesDownloaded - Stats.BytesDownloadedPrev) / 2; // Change this (2 seconds) if you change scheduler
            if ( curSeconds > 0 ) 
                Stats.AvgRate   = (int) (totalBytesDownloaded / curSeconds);
            
            if ( torrent.data.totalSize - Stats.BytesDownloaded == 0 )
            {
                Stats.ETA       = 0;
                Stats.AvgETA    = 0;
            } 
            else
            {
                if ( Stats.BytesDownloaded - Stats.BytesDownloadedPrev == 0 )
                    Stats.ETA  *= 2; // Kind of infinite | int overflow nice
                else 
                    Stats.ETA   = (int) ( (torrent.data.totalSize - Stats.BytesDownloaded) / ((Stats.BytesDownloaded - Stats.BytesDownloadedPrev) / 2) );

                if ( Stats.BytesDownloaded  == 0 )
                    Stats.AvgETA*= 2; // Kind of infinite
                else
                    if ( curSeconds > 0 ) Stats.AvgETA = (int) ( (torrent.data.totalSize - Stats.BytesDownloaded) / (Stats.BytesDownloaded / curSeconds ) );
            }
                                
            if ( Stats.DownRate > Stats.MaxRate ) Stats.MaxRate = Stats.DownRate;

            Stats.BytesDownloadedPrev   = totalBytesDownloaded;
            Stats.PeersTotal            = peers.Count;

            // Stats -> UI
            Options.StatsCallback?.BeginInvoke(Stats, null, null);

            // Stats -> Log
            if ( Options.LogStats )
            {
                Log($"[STATS] [INQUEUE: {String.Format("{0,3}",Stats.PeersInQueue)}]\t[DROPPED: {String.Format("{0,3}",Stats.PeersDropped)}]\t[CONNECTS: {String.Format("{0,3}",openConnections)}]\t[CONNECTING: {String.Format("{0,3}",Stats.PeersConnecting)}]\t[FAIL1: {String.Format("{0,3}",Stats.PeersFailed1)}]\t[FAIL2: {String.Format("{0,3}",Stats.PeersFailed2)}]\t[READY: {String.Format("{0,3}",Stats.PeersConnected)}]\t[CHOKED: {String.Format("{0,3}",Stats.PeersChoked)}]\t[UNCHOKED: {String.Format("{0,3}",Stats.PeersUnChoked)}]\t[DOWNLOADING: {String.Format("{0,3}",Stats.PeersDownloading)}]");
                Log($"[STATS] [CUR MAX: {String.Format("{0:n0}", (Stats.MaxRate / 1024)) + " KB/s"}]\t[DOWN CUR: {String.Format("{0:n0}", (Stats.DownRate / 1024)) + " KB/s"}]\t[DOWN AVG: {String.Format("{0:n0}", (Stats.AvgRate / 1024)) + " KB/s"}]\t[ETA CUR: {TimeSpan.FromSeconds(Stats.ETA).ToString(@"hh\:mm\:ss")}]\t[ETA AVG: {TimeSpan.FromSeconds(Stats.AvgETA).ToString(@"hh\:mm\:ss")}]\t[ETA R: {TimeSpan.FromSeconds((Stats.ETA + Stats.AvgETA)/2).ToString(@"hh\:mm\:ss")}]");
                Log($"[STATS] [TIMEOUTS: {String.Format("{0,4}",pieceTimeouts)}]\t[ALREADYRECV: {String.Format("{0,3}",pieceAlreadyRecv)}]\t[REJECTED: {String.Format("{0,3}",pieceRejected)}]\t[SHA1FAILS:{String.Format("{0,3}",sha1Fails)}]\t[DROPPED BYTES: {Utils.BytesToReadableString(Stats.BytesDropped)}]\t[DHTPEERS: {dhtPeers}]");
            }
        }

        // Main Threads         [Trackers | Peers]
        private void PeerBeggar()
        {
            try
            {
                Log("[PEER-BEGGAR] " + status);

                for (int i=0; i<trackers.Count; i++)
                {
                    int noCache = i;
                    ThreadPool.QueueUserWorkItem(new WaitCallback(delegate(object state) { FillPeersFromTracker(noCache); }), null);
                }

            } catch (ThreadAbortException) {
            } catch (Exception e) { Log($"[PEER-BEGGAR] PeerBeggar(), Msg: {e.StackTrace}"); }
        }
        private void Beggar()
        {
            try
            {
                Utils.TimeBeginPeriod(1);
                
                Stats.MaxRate           = 0;
                curSeconds              = 0;
                metadataLastRequested   = DateTime.UtcNow.Ticks;

                log.RestartTime();
                if ( Options.EnableDHT ) { logDHT.RestartTime(); dht.Start(); }
                PeerBeggar();

                Log("[BEGGAR  ] " + status);

                while ( status == Status.RUNNING )
                {
                    lock ( lockerPeers )
                    {
                        openConnections = 0;

                        // Request Piece | Count Connections
                        foreach (Peer peer in peers)
                        {
                            if ( status != Status.RUNNING ) break;
                            switch ( peer.status )
                            {
                                case Peer.Status.CONNECTING:
                                case Peer.Status.CONNECTED:
                                case Peer.Status.DOWNLOADING:
                                    openConnections++;

                                    break;
                                case Peer.Status.READY:
                                    openConnections++;

                                    // Don't keep too many choked connections open
                                    if ( Stats.PeersChoked > Options.MaxConnections / 2 && !peer.stageYou.unchoked && Stats.PeersInQueue > Options.MaxConnections / 4 )
                                    {
                                        Log($"[DROP] Choked Peer {peer.host}");
                                        Stats.PeersChoked--;
                                        peer.status = Peer.Status.FAILED2;
                                        peer.Disconnect();
                                    }

                                    break;
                            }
                        }

                        // Request Piece | New Connections 
                        foreach (Peer peer in peers)
                        {
                            if ( status != Status.RUNNING ) break;
                            switch ( peer.status )
                            {
                                case Peer.Status.NEW:
                                    if ( openConnections < Options.MaxConnections )
                                    {
                                        openConnections++;
                                        peer.status = Peer.Status.CONNECTING;
                                        ThreadPool.QueueUserWorkItem(new WaitCallback(delegate(object state) { peer.Run(); }), null);
                                    }

                                    break;
                                case Peer.Status.READY:

                                    RequestPiece(peer);

                                    break;
                                case Peer.Status.DOWNLOADING:
                                    break;
                            }
                        }

                        // Scheduler
                        if ( log.GetTime() - (curSeconds * 1000) > 990 )
                        {
                            // Scheduler Every 1 Second
                            curSecondTicks = DateTime.UtcNow.Ticks;
                            curSeconds++;

                            // Scheduler Every 2 Seconds    [Stats | Clean Failed Peers]
                            if ( curSeconds %  2 == 0 )
                            {
                                // Check Request Timeouts
                                if ( torrent.metadata.isDone )
                                    CheckRequestTimeouts();
                                else
                                    if ( curSecondTicks - metadataLastRequested > Options.MetadataTimeout * 10000 ) { torrent.metadata.parallelRequests += 2; Log($"[REQ ] [M] Timeout"); }

                                if ( torrent.metadata.isDone ) FillStats();

                                if ( Options.EnableDHT )
                                    if (        dht.status == DHT.Status.RUNNING && Stats.PeersInQueue > Options.MaxConnections ) 
                                        dht.Stop();
                                    else if (   dht.status == DHT.Status.STOPPED && Stats.PeersInQueue < (Options.MaxConnections / 3) )
                                        dht.Start();
                            }

                            // Scheduler Every 5 Seconds    [Peers MSG Keep Alive]
                            if ( curSeconds %  5 == 0 )
                            {
                                // Keep Alives / Interested
                                foreach (Peer peer in peers)
                                {
                                    switch ( peer.status )
                                    {
                                        case Peer.Status.READY:
                                            if ( (curSecondTicks - peer.lastAction) / 10000 > 3000 )
                                                peer.SendKeepAlive();

                                            break;
                                    }
                                }
                            }

                            // Scheduler Every 16 Seconds   [Peers MSG Intrested | Trackers Announce]
                            if ( curSeconds % 16 == 0 )
                            {
                                // Client-> Peers [MSG Intrested]
                                foreach (Peer peer in peers)
                                {
                                    switch ( peer.status )
                                    {
                                        case Peer.Status.READY:

                                            if ( !peer.stageYou.unchoked )
                                                peer.SendMessage(Peer.Messages.INTRESTED, false, null);

                                            break;
                                    }
                                }
                            }

                            // Scheduler Every 40 Seconds   [DHT Clear Cache Peers]
                            if ( curSeconds % 40 == 0 )
                            {
                                // Client -> Trackers [Announce]
                                PeerBeggar();

                                if ( Options.EnableDHT && Stats.PeersInQueue < (Options.MaxConnections / 2) )
                                    dht.ClearCachedPeers();
                            }

                        } // Scheduler [Every Second]

                    } // Lock Peers

                    Thread.Sleep(15);

                } // While

                if ( Options.EnableDHT ) dht.Stop();
                Log("[BEGGAR  ] " + status);

                // Clean Up
                foreach (Peer peer in peers)
                    peer.Disconnect();

                torrent.Dispose();
                log.Dispose();

            } catch (ThreadAbortException) {
            } catch (Exception e) { Options.StatusCallback?.BeginInvoke(2, e.Message, null, null); Log($"[BEGGAR] Beggar(), Msg: {e.Message}\r\n{e.StackTrace}"); }

            Utils.TimeEndPeriod(1);
        }

        private void CheckRequestTimeouts()
        {
            lock ( lockerTorrent )
            {
                for (int i=torrent.data.pieceRequstes.Count-1; i>=0; i--)
                {
                    if ( curSecondTicks - torrent.data.pieceRequstes[i].timestamp > Options.PieceTimeout * 10000 )
                    {
                        // !(Piece Done | Block Done)
                        bool containsKey = torrent.data.pieceProgress.ContainsKey(torrent.data.pieceRequstes[i].piece);
                        if ( !(( !containsKey && torrent.data.progress.GetBit(torrent.data.pieceRequstes[i].piece) ) 
                             ||(  containsKey && torrent.data.pieceProgress[torrent.data.pieceRequstes[i].piece].progress.GetBit(torrent.data.pieceRequstes[i].block))) )
                        { 
                            torrent.data.pieceRequstes[i].peer.PiecesTimeout++;
                            Log($"[{torrent.data.pieceRequstes[i].peer.host.PadRight(15, ' ')}] [REQT][P]\tPiece: {torrent.data.pieceRequstes[i].piece} Size: {torrent.data.pieceRequstes[i].size} Block: {torrent.data.pieceRequstes[i].block} Offset: {torrent.data.pieceRequstes[i].block * torrent.data.blockSize} Requests: {torrent.data.pieceRequstes[i].peer.PiecesRequested} Timeouts: {torrent.data.pieceRequstes[i].peer.PiecesTimeout} Piece timeout");

                            if ( torrent.data.pieceRequstes[i].peer.status != Peer.Status.READY && torrent.data.pieceRequstes[i].peer.PiecesTimeout >= Options.RequestBlocksPerPeer )
                                {  torrent.data.pieceRequstes[i].peer.status  = Peer.Status.FAILED2; torrent.data.pieceRequstes[i].peer.Disconnect(); }
                            else
                                 torrent.data.pieceRequstes[i].peer.status  = Peer.Status.READY;

                            if ( containsKey ) torrent.data.pieceProgress[torrent.data.pieceRequstes[i].piece].requests.UnSetBit(torrent.data.pieceRequstes[i].block);

                            torrent.data.requests.UnSetBit(torrent.data.pieceRequstes[i].piece);
                            pieceTimeouts++;
                        }

                        torrent.data.pieceRequstes.RemoveAt(i);
                    }
                }
            }
        }

        // Main Implementation  [RequestPiece | SavePiece]
        private void RequestPiece(Peer peer)
        {
            int piece, block, pieceSize, blockSize;

            // Metadata Requests (Until Done)
            if ( !torrent.metadata.isDone )
            {
                if ( peer.stageYou.metadataRequested || peer.stageYou.extensions.ut_metadata == 0 ) return;
                if ( torrent.metadata.parallelRequests < 1 ) return;


                if ( torrent.metadata.totalSize == 0 )
                {   
                    torrent.metadata.parallelRequests -= 2;
                    peer.RequestMetadata(0, 1);
                    Log($"[{peer.host.PadRight(15, ' ')}] [REQ ][M]\tPiece: 0, 1");
                }
                else
                {
                    piece = torrent.metadata.progress.GetFirst0();
                    if ( piece == -1 ) return;

                    int piece2 = torrent.metadata.progress.GetFirst0(piece + 1);

                    if ( piece > torrent.metadata.pieces - 1 || piece2 > torrent.metadata.pieces - 1 ) return;

                    if ( piece2 != -1 )
                    {
                        torrent.metadata.parallelRequests -= 2;
                        peer.RequestMetadata(piece, piece2);
                    }
                    else
                    {
                        torrent.metadata.parallelRequests--;
                        peer.RequestMetadata(piece);
                    }

                    Log($"[{peer.host.PadRight(15, ' ')}] [REQ ][M]\tPiece: {piece},{piece2}");
                }

                metadataLastRequested = DateTime.UtcNow.Ticks;
                peer.stageYou.metadataRequested = true;

                return;
            }

            // Torrent Requests
            // TODO: start/end-game mode
            if ( !peer.stageYou.unchoked || peer.stageYou.haveNone || (!peer.stageYou.haveAll && peer.stageYou.bitfield == null) ) return;

            List<Tuple<int, int, int>> requests = new List<Tuple<int, int, int>>();

            for (int i=0; i<Options.RequestBlocksPerPeer; i++)
            {
                if ( peer.stageYou.haveAll )
                    piece = torrent.data.requests.GetFirst0();

                else
                    piece = torrent.data.requests.GetFirst01(peer.stageYou.bitfield);
                
                if ( piece == -1 ) break;

                bool containsKey;
                lock ( lockerTorrent ) containsKey = torrent.data.pieceProgress.ContainsKey(piece);
                if ( !containsKey )
                {
                    block = 0;
                    PieceProgress pp = new PieceProgress(torrent.data, piece);
                    torrent.data.pieceProgress.Add(piece, pp);
                } else
                {
                    block = torrent.data.pieceProgress[piece].requests.GetFirst0();
                    if ( block == -1 ) { Log($"Shouldn't be here! Piece: {piece}"); break; }
                }
                
                torrent.data.pieceProgress[piece].requests.SetBit(block);
                if ( torrent.data.pieceProgress[piece].requests.GetFirst0() == -1 ) torrent.data.requests.SetBit(piece);

                if ( block != torrent.data.blocks - 1 )
                    blockSize = torrent.data.blockSize;
                else
                    blockSize = torrent.data.blockLastSize;

                if ( piece == torrent.data.pieces - 1 ) // Last Piece (need to check also in case of last block size?)
                {
                    pieceSize = torrent.data.totalSize % torrent.data.pieceSize != 0 ?  (int) (torrent.data.totalSize % torrent.data.pieceSize) : torrent.data.pieceSize;

                    if (block == pieceSize / torrent.data.blockSize )
                        blockSize= pieceSize % torrent.data.blockSize;
                }

                Log($"[{peer.host.PadRight(15, ' ')}] [REQ ][P]\tPiece: {piece} Size: {blockSize} Block: {block} Offset: {block * torrent.data.blockSize} Requests: {peer.PiecesRequested} Timeouts: {peer.PiecesTimeout}");

                requests.Add(new Tuple<int, int, int>(piece, block * torrent.data.blockSize, blockSize));
                lock ( lockerTorrent ) torrent.data.pieceRequstes.Add( new PieceRequest(DateTime.UtcNow.Ticks, peer, piece, block, blockSize) );
            }
                
            if ( requests.Count > 0 ) peer.RequestPiece(requests);
        }
        private void SavePiece(byte[] data, int piece, int size)
        {
            if ( size > torrent.file.pieceLength ) { Log("[SAVE] pieceSize > chunkSize"); return; }

            long firstByte  = piece * torrent.file.pieceLength;
            long ends       = firstByte + size;
            long sizeLeft   = size;
            int writePos    = 0;
            long curSize    = 0;

            for (int i=0; i<torrent.file.lengths.Count; i++)
            {
                curSize += torrent.file.lengths[i];
                if ( firstByte < curSize ) {
		            int writeSize 	= (int) Math.Min(sizeLeft, curSize - firstByte);
                    int chunkId     = (int) (((firstByte + torrent.file.pieceLength - 1) - (curSize - torrent.file.lengths[i]))/torrent.file.pieceLength);

		            if ( firstByte == curSize - torrent.file.lengths[i] )
                    {
                        Log("[SAVE] F file:\t\t" + (i + 1) + ", chunkId:\t\t" +     0 +     ", pos:\t\t" + writePos + ", len:\t\t" + writeSize);
                        torrent.data.files[i].WriteFirst(data, writePos, writeSize);
                    } else if ( ends < curSize  ) {
                        Log("[SAVE] M file:\t\t" + (i + 1) + ", chunkId:\t\t" + chunkId +   ", pos:\t\t" + writePos + ", len:\t\t" + torrent.file.pieceLength + " | " + writeSize);
                        torrent.data.files[i].Write(chunkId,data);
                    } else if ( ends >= curSize ) {
                        Log("[SAVE] L file:\t\t" + (i + 1) + ", chunkId:\t\t" + chunkId +   ", pos:\t\t" + writePos + ", len:\t\t" + writeSize);
                        torrent.data.files[i].WriteLast(chunkId, data, writePos, writeSize);
                    }

                    if ( ends - 1 < curSize ) break;

		            firstByte = curSize;
                    sizeLeft -= writeSize;
                    writePos += writeSize;
                }
            }
        }

        // Peer Callbacks       [Meta Data]
        private void MetadataReceived(byte[] data, int piece, int offset, int totalSize, Peer peer)
        {
            Log($"[{peer.host.PadRight(15, ' ')}] [RECV][M]\tPiece: {piece} Offset: {offset} Size: {totalSize}");

            lock ( lockerMetadata )
            {
                torrent.metadata.parallelRequests  += 2;
                peer.stageYou.metadataRequested     = false;

                if ( torrent.metadata.totalSize == 0 )
                {
                    torrent.metadata.totalSize  = totalSize;
                    torrent.metadata.progress   = new BitField((totalSize/Peer.MAX_DATA_SIZE) + 1);
                    torrent.metadata.pieces     = (totalSize/Peer.MAX_DATA_SIZE) + 1;
                    if ( torrent.file.name != null ) 
                        torrent.metadata.file       = new PartFile(Utils.FindNextAvailablePartFile(Path.Combine(Options.DownloadPath, string.Join("_", torrent.file.name.Split(Path.GetInvalidFileNameChars())) + ".torrent")), Peer.MAX_DATA_SIZE, totalSize);
                    else
                        torrent.metadata.file       = new PartFile(Utils.FindNextAvailablePartFile(Path.Combine(Options.DownloadPath, "metadata" + rnd.Next(10000) + ".torrent")), Peer.MAX_DATA_SIZE, totalSize);
                }

                if ( torrent.metadata.progress.GetBit(piece) ) { Log($"[{peer.host.PadRight(15, ' ')}] [RECV][M]\tPiece: {piece} Already received"); return; }

                if ( piece == torrent.metadata.pieces - 1 ) 
                {
                    torrent.metadata.file.WriteLast(piece, data, offset, data.Length - offset);
                } else
                {
                    torrent.metadata.file.Write(piece, data, offset);
                }

                torrent.metadata.progress.SetBit(piece);
            
                if ( torrent.metadata.progress.setsCounter == torrent.metadata.pieces )
                {
                    // TODO: Validate Torrent's SHA-1 Hash with Metadata Info
                    torrent.metadata.parallelRequests = -1000;
                    Log($"Creating Metadata File {torrent.metadata.file.FileName}");
                    torrent.metadata.file.CreateFile();
                    torrent.FillFromMetadata();
                    peerOpt.Pieces = torrent.data.pieces;
                    Options.TorrentCallback?.BeginInvoke(torrent, null, null);

                    if ( Options.EnableDHT ) logDHT.RestartTime();
                    log.RestartTime();
                    curSeconds = 0;
                    torrent.metadata.isDone = true;
                }
            }
        }
        private void MetadataRejected(int piece, string src)
        {
            torrent.metadata.parallelRequests += 2;
            Log($"[{src.PadRight(15, ' ')}] [RECV][M]\tPiece: {piece} Rejected");
        }

        // Peer Callbacks       [Torrent Data]
        private void PieceReceived(byte[] data, int piece, int offset, Peer peer)
        {
            // [Already Received | SHA-1 Validation Failed] => leave
            int  block = offset / torrent.data.blockSize;
            bool containsKey;
            bool pieceProgress;
            bool blockProgress = false;

            lock ( lockerTorrent )
            {   
                containsKey                     = torrent.data.pieceProgress.ContainsKey(piece);
                pieceProgress                   = torrent.data.progress.GetBit(piece);
                if (containsKey) blockProgress  = torrent.data.pieceProgress[piece].progress.GetBit(block);
            }
                
            // Piece Done | Block Done
            if (   (!containsKey && pieceProgress )     // Piece Done
                || ( containsKey && blockProgress ) )   // Block Done
            { 
                Stats.BytesDropped += data.Length; 
                pieceAlreadyRecv++;
                Log($"[{peer.host.PadRight(15, ' ')}] [RECV][P]\tPiece: {piece} Size: {data.Length} Block: {block} Offset: {offset} Already received"); 

                return; 
            }

            Log($"[{peer.host.PadRight(15, ' ')}] [RECV][P]\tPiece: {piece} Size: {data.Length} Block: {block} Offset: {offset} Requests: {peer.PiecesRequested} Timeouts: {peer.PiecesTimeout}");
            Stats.BytesDownloaded += data.Length;

            // Parse Block Data to Piece Data
            Buffer.BlockCopy(data, 0, torrent.data.pieceProgress[piece].data, offset, data.Length);

            // SetBit Block | Leave if more blocks required for Piece
            torrent.data.pieceProgress[piece].progress.     SetBit(block);
            torrent.data.pieceProgress[piece].requests.     SetBit(block); // In case of Timed out (to avoid re-requesting)
            if ( torrent.data.pieceProgress[piece].progress.GetFirst0() != -1 ) return;
            
            // SHA-1 Validation for Piece Data | Failed? -> Re-request whole Piece! | Lock, No thread-safe!
            byte[] pieceHash;
            lock ( lockerTorrent ) pieceHash = sha1.ComputeHash(torrent.data.pieceProgress[piece].data);
            if ( !Utils.ArrayComp(torrent.file.pieces[piece], pieceHash) )
            {
                Log($"[{peer.host.PadRight(15, ' ')}] [RECV][P]\tPiece: {piece} Size: {data.Length} Block: {block} Offset: {offset} Size2: {torrent.data.pieceProgress[piece].data.Length} SHA-1 validation failed");

                Stats.BytesDropped      += torrent.data.pieceProgress[piece].data.Length;
                Stats.BytesDownloaded   -= torrent.data.pieceProgress[piece].data.Length;
                sha1Fails++;

                torrent.data.requests.      UnSetBit(piece);
                torrent.data.pieceProgress. Remove(piece);

                return;
            }
            
            // Save Piece in PartFiles [No lock | Thread-safe]
            SavePiece(torrent.data.pieceProgress[piece].data, piece, torrent.data.pieceProgress[piece].data.Length);

            // [SetBit for Progress | Remove Block Progress] | Done => CreateFiles
            torrent.data.progress.      SetBit(piece);
            torrent.data.requests.      SetBit(piece); // In case of Timed out (to avoid re-requesting)
            torrent.data.pieceProgress. Remove(piece);
            
            if ( torrent.data.progress.GetFirst0() == - 1 ) // just compare with pieces size
            {
                Log("[FINISH] Creating Files ...");
                for (int i = 0; i < torrent.data.files.Count; i++)
                    torrent.data.files[i].CreateFile();

                torrent.data.isDone = true;
                status              = Status.STOPPED;
            }
        }
        private void PieceRejected(int piece, int offset, int size, Peer peer)
        {
            int block = offset / torrent.data.blockSize;
            Log($"[{peer.host.PadRight(15, ' ')}] [RECV][P][REJECTED]\tPiece: {piece} Size: {size} Block: {block} Offset: {offset}");
            
            lock ( lockerTorrent )
            {
                bool containsKey = torrent.data.pieceProgress.ContainsKey(piece);

                // !(Piece Done | Block Done)
                if ( !(( !containsKey && torrent.data.progress.GetBit(piece) ) 
                    || (  containsKey && torrent.data.pieceProgress[piece].progress.GetBit(block))) )
                { 
                    if ( containsKey ) torrent.data.pieceProgress[piece].requests.UnSetBit(block);
                    torrent.data.requests.UnSetBit(piece);
                }
            }

            pieceRejected++;
        }
        
        // Misc
        private void Log(string msg) { if (Options.Verbosity > 0) log.Write($"[TorSwarm] {msg}"); }

    }
}