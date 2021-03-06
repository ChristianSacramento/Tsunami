﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Timers;
using NLog;
using Microsoft.Owin.Hosting;
using System.IO;
using System.Collections.ObjectModel;
using System.Threading;
using Tsunami.Core;
//using Microsoft.Owin;

//[assembly: OwinStartup(typeof(Tsunami.www.Startup))]

namespace Tsunami
{
    public static class SessionManager
    {
        private static Logger log = LogManager.GetLogger("SessionManager");
        private static Core.Session _torrentSession = new Core.Session();

        //save_resume_variables
        private static int outstanding_resume_data = 0;
        private static AutoResetEvent no_more_data = new AutoResetEvent(false);
        private static bool no_more_resume = false;

        //window sliding variables
        private static int start_window = 0;
        private static int end_window = Int32.MaxValue;

        private static System.Timers.Timer _dispatcherTimer = new System.Timers.Timer();
        private static System.Timers.Timer _sessionStatusDispatcherTimer = new System.Timers.Timer();

        private static IDisposable webServer;

        public static EventHandler<EventsArgs.OnTorrentUpdatedEventArgs> TorrentUpdated;
        public static EventHandler<EventsArgs.OnTorrentAddedEventArgs> TorrentAdded;
        public static EventHandler<EventsArgs.OnTorrentRemovedEventArgs> TorrentRemoved;
        public static EventHandler<EventsArgs.OnSessionStatisticsEventArgs> SessionStatisticsUpdate;
        public static EventHandler<Models.ErrorCode> TorrentError;

        private static ConcurrentDictionary<string, Core.TorrentHandle> TorrentHandles = new ConcurrentDictionary<string, Core.TorrentHandle>();
        private static Dictionary<Type, Action<Object>> Alert2Func = new Dictionary<Type, Action<Object>>();

        public static void Initialize()
        {
            Settings.Logger.Inizialize();

            // web server
            if (Settings.User.StartWebOnAppLoad)
            {
                startWeb();
            }

            if (File.Exists(".session_state"))
            {
                var data = File.ReadAllBytes(".session_state");
                using (var entry = Core.Util.lazy_bdecode(data))
                {
                    _torrentSession.load_state(entry);
                }
            }

            _torrentSession.start_dht();
            //_torrentSession.start_lsd();
            _torrentSession.start_natpmp();
            _torrentSession.start_upnp();

            Alert2Func[typeof(Core.torrent_added_alert)] = a => OnTorrentAddAlert((Core.torrent_added_alert)a);
            Alert2Func[typeof(Core.state_update_alert)] = a => OnTorrentUpdateAlert((Core.state_update_alert)a);
            //Alert2Func[typeof(Core.torrent_paused_alert)] = a => OnTorrentPausedAlert((Core.torrent_paused_alert)a);
            //Alert2Func[typeof(Core.torrent_resumed_alert)] = a => OnTorrentResumedAlert((Core.torrent_resumed_alert)a);
            //Alert2Func[typeof(Core.torrent_removed_alert)] = a => OnTorrentRemovedAlert((Core.torrent_removed_alert)a); // torrent removed from torrentSession, always ok
            //Alert2Func[typeof(Core.torrent_deleted_alert)] = a => OnTorrentDeletedAlert((Core.torrent_deleted_alert)a); // finished deleting file for removed torrent
            Alert2Func[typeof(Core.torrent_error_alert)] = a => OnTorrentErrorAlert((Core.torrent_error_alert)a);
            //Alert2Func[typeof(Core.stats_alert)] = a => OnStatsAlert((Core.stats_alert)a);
            Alert2Func[typeof(Core.dht_stats_alert)] = a => OnDhtStatsAlert((Core.dht_stats_alert)a);

            Alert2Func[typeof(Core.save_resume_data_alert)] = a => OnSaveResumeDataAlert((Core.save_resume_data_alert)a);
            Alert2Func[typeof(Core.save_resume_data_failed_alert)] = a => OnSaveResumeDataFailedAlert((Core.save_resume_data_failed_alert)a);
            Alert2Func[typeof(Core.piece_finished_alert)] = a => OnPieceFinishedAlert((Core.piece_finished_alert)a);

            _dispatcherTimer.Elapsed += new ElapsedEventHandler(dispatcherTimer_Tick);
            _dispatcherTimer.Interval = Settings.Application.DISPATCHER_INTERVAL;
            _dispatcherTimer.Start();

            _sessionStatusDispatcherTimer.Elapsed += new ElapsedEventHandler(sessionStatusDispatcher_Tick);
            _sessionStatusDispatcherTimer.Interval = 1000;
            _sessionStatusDispatcherTimer.Start();

            // http://www.libtorrent.org/reference-Alerts.html

            var alertMask = Core.AlertMask.error_notification
                    | Core.AlertMask.peer_notification
                    | Core.AlertMask.port_mapping_notification
                    | Core.AlertMask.storage_notification
                    | Core.AlertMask.tracker_notification
                    | Core.AlertMask.status_notification
                    | Core.AlertMask.ip_block_notification
                    | Core.AlertMask.progress_notification
                    | Core.AlertMask.stats_notification
                    ;
            _torrentSession.set_alert_mask(alertMask);
            _torrentSession.set_alert_dispatch(HandleAlertCallback);

        }

        private static void OnPieceFinishedAlert(Core.piece_finished_alert a)
        {
            
        }

        private static void OnDhtStatsAlert(Core.dht_stats_alert a)
        {
            var aaa = a.active_requests;

        }

        private static void OnSaveResumeDataAlert(Core.save_resume_data_alert a)
        {
            var h = a.handle;
            string newfilePath = ("./Fastresume/" + h.info_hash().ToString() + ".fastresume");
            var data = Core.Util.bencode(a.resume_data);
            using (var bw = new BinaryWriter(new FileStream(newfilePath, FileMode.OpenOrCreate)))
            {
                bw.Write(data);
                bw.Close();
            }
            Interlocked.Decrement(ref outstanding_resume_data);
            if (outstanding_resume_data == 0 && no_more_resume)
                no_more_data.Set();
        }

        private static void OnSaveResumeDataFailedAlert(Core.save_resume_data_failed_alert a)
        {
            Interlocked.Decrement(ref outstanding_resume_data);
            if (outstanding_resume_data == 0 && no_more_resume)
                no_more_data.Set();
        }

        public static void LoadFastResumeData()
        {
            if (Directory.Exists("Fastresume"))
            {
                string[] files = Directory.GetFiles("Fastresume", "*.fastresume");
                foreach (string s in files)
                {
                    var data = File.ReadAllBytes(s);
                    var info_hash = Path.GetFileNameWithoutExtension(s);
                    var filename = "Fastresume/" + info_hash + ".torrent";
                    Core.TorrentInfo ti;
                    if (File.Exists(filename))
                        ti = new Core.TorrentInfo(filename);
                    else
                        ti = new Core.TorrentInfo(new Core.Sha1Hash(info_hash));
                    using (var atp = new Core.AddTorrentParams())
                    using (ti)
                    {
                        atp.ti = ti;
                        atp.save_path = Settings.User.PathDownload;
                        atp.resume_data = (sbyte[])(Array)data;
                        atp.flags &= ~Core.ATPFlags.flag_auto_managed; // remove auto managed flag
                        atp.flags &= ~Core.ATPFlags.flag_paused; // remove pause on added torrent
                        _torrentSession.async_add_torrent(atp);
                    }
                }
            }
            else
            {
                Directory.CreateDirectory("Fastresume");
            }
        }

        public static void startWeb()
        {
            Uri baseAddress = new Uri(string.Format("http://{0}:{1}", Settings.User.WebAddress, Settings.User.WebPort));
            webServer = WebApp.Start<www.Startup>(baseAddress.ToString());
        }

        public static void stopWeb()
        {
            webServer?.Dispose();
            webServer = null;
        }

        public static void Terminate()
        {
            _sessionStatusDispatcherTimer.Stop();
            _sessionStatusDispatcherTimer.Dispose();
            _dispatcherTimer.Stop();
            _dispatcherTimer.Dispose();
            stopWeb();
                        
            _torrentSession.pause();

            /* http://libtorrent.org/reference-Core.html#save_resume_data() */
            foreach (var item in TorrentHandles)
            {
                if (item.Value.is_valid())
                {
                    Core.TorrentStatus ts = item.Value.status();
                    if (ts.has_metadata && ts.need_save_resume)
                    {
                        /* http://libtorrent.org/reference-Core.html#save_resume_flags_t */
                        item.Value.save_resume_data(1 | 2 | 4);
                        ++outstanding_resume_data;
                    }
                }
            }
            no_more_resume = true;
            if(outstanding_resume_data != 0)
                no_more_data.WaitOne();
            TerminateSaveResume();
        }

        private static void TerminateSaveResume()
        {
            _torrentSession.clear_alert_dispatch();
            using (var entry = _torrentSession.save_state(0xfffffff))
            {
                var data = Core.Util.bencode(entry);
                File.WriteAllBytes(".session_state", data);
            }
            _torrentSession?.Dispose();
            _torrentSession = null;
        }

        private static Core.TorrentHandle getTorrentHandle(string hash)
        {
            log.Trace("requested getTorrentHandle({0})", hash);
            Core.TorrentHandle th;
            if (TorrentHandles.TryGetValue(hash, out th))
            {
                return th;
            }
            else
            {
                string smgs = "cannot find requested torrent in Tsunami";
                log.Error(smgs);
                throw new Exception(smgs);
            }
        }

        public static Models.TorrentStatus getTorrentStatus(string hash)
        {
            Core.TorrentHandle th;
            log.Trace("requested getTorrentStatus({0})", hash);
            if (TorrentHandles.TryGetValue(hash, out th))
            {
                return new Models.TorrentStatus(th.status());
            }
            else
            {
                string smgs = "cannot find requested torrent in Tsunami";
                log.Error(smgs);
                throw new Exception(smgs);
            }
        }

        public static List<Models.FileEntry> getTorrentFiles(string hash)
        {
            List<Models.FileEntry> feList = new List<Models.FileEntry>();
            Models.FileEntry fe;

            Core.TorrentHandle th = getTorrentHandle(hash);
            Core.TorrentInfo ti = th.torrent_file();
            for (int i = 0; i <= ti.num_files() - 1; i++)
            {
                fe = new Models.FileEntry(ti.files().at(i));
                fe.FileName = ti.files().file_name(i);
                fe.IsValid = ti.files().is_valid();
                fe.PieceSize = ti.piece_size(i);
                //ti.files().name(); ???
                //ti.trackers();
                feList.Add(fe);
            }
            return feList;
        }

        public static List<Models.TorrentStatus> getTorrentStatusList()
        {
            _dispatcherTimer.Stop();
            _sessionStatusDispatcherTimer.Stop();
            List<Models.TorrentStatus> thl = new List<Models.TorrentStatus>();
            foreach (KeyValuePair<string, Core.TorrentHandle> item in TorrentHandles)
            {
                Core.TorrentHandle th = item.Value;
                thl.Add(new Models.TorrentStatus(th.status()));// Models.TorrentHandle(item.Value));
            }
            _dispatcherTimer.Start();
            _sessionStatusDispatcherTimer.Start();
            return thl;
        }

        public static void addTorrent(string filename)
        {
            var data = File.ReadAllBytes(filename);
            string newfilePath;
            using (var atp = new Core.AddTorrentParams())
            using (var ti = new Core.TorrentInfo(filename))
            {
                atp.save_path = Settings.User.PathDownload;
                atp.ti = ti;
                atp.flags &= ~Core.ATPFlags.flag_auto_managed; // remove auto managed flag
                atp.flags &= ~Core.ATPFlags.flag_paused; // remove pause on added torrent
                atp.flags &= ~Core.ATPFlags.flag_use_resume_save_path; // 
                newfilePath = "./Fastresume/" + ti.info_hash().ToString() + ".torrent";
                if (!File.Exists(newfilePath))
                {
                    using (var bw = new BinaryWriter(new FileStream(newfilePath, FileMode.Create)))
                    {
                        bw.Write(data);
                        bw.Close();
                    }
                }
                _torrentSession.async_add_torrent(atp);
            }
        }

        public static void addTorrent(byte[] buffer)
        {
            using (var atp = new Core.AddTorrentParams())
            using (var ti = new Core.TorrentInfo(buffer))
            {
                atp.save_path = Settings.User.PathDownload;
                atp.ti = ti;
                atp.flags &= ~Core.ATPFlags.flag_auto_managed; // remove auto managed flag
                atp.flags &= ~Core.ATPFlags.flag_paused; // remove pause on added torrent
                _torrentSession.async_add_torrent(atp);
            }
        }

        public static void deleteTorrent(string hash, bool deleteFileToo = false)
        {
            log.Trace("requested delete({0}, deleteFile:{1})", hash, deleteFileToo);
            Core.TorrentHandle th = getTorrentHandle(hash);
            _torrentSession.remove_torrent(th, Convert.ToInt32(deleteFileToo));
            TorrentHandles.TryRemove(hash, out th);
            File.Delete(Environment.CurrentDirectory + "./Fastresume/" + hash + ".fastresume");
            File.Delete(Environment.CurrentDirectory + "./Fastresume/" + hash + ".torrent");
        }

        public static bool pauseTorrent(string hash)
        {
            log.Trace("requested pause({0})", hash);
            Core.TorrentHandle th = getTorrentHandle(hash);
            th.pause();
            return (th.status().paused == true);
        }

        public static bool resumeTorrent(string hash)
        {
            log.Trace("requested resume({0})", hash);
            Core.TorrentHandle th = getTorrentHandle(hash);
            th.resume();
            return (th.status().paused == true);
        }

        private static void dispatcherTimer_Tick(object sender, ElapsedEventArgs e)
        {
            _torrentSession.post_torrent_updates();
            _torrentSession.post_dht_stats();
            //_torrentSession.post_session_stats();
        }

        private static void sessionStatusDispatcher_Tick(object sender, ElapsedEventArgs e)
        {
            var evnt = new EventsArgs.OnSessionStatisticsEventArgs(_torrentSession.status());

            // notify web
            if (webServer != null)
            {
                var context = Microsoft.AspNet.SignalR.GlobalHost.ConnectionManager.GetHubContext<www.SignalRHub>();
                context.Clients.All.notifySessionStatistics(evnt);
            }

            // invoke event
            SessionStatisticsUpdate?.Invoke(null, evnt);
        }

        private static void HandleAlertCallback(Core.Alert a)
        {
            using (a)
            {
                Action<object> run;
                if (Alert2Func.TryGetValue(a.GetType(), out run))
                {
                    run(a);
                }
            }
        }

        private static void OnTorrentUpdateAlert(Core.state_update_alert a)
        {
            foreach (Core.TorrentStatus ts in a.status)
            {
                var stat = "Paused";
                if (!ts.paused)
                {
                    stat = Utils.GiveMeStateFromEnum(ts.state);
                }
                var evnt = new EventsArgs.OnTorrentUpdatedEventArgs(ts);
                evnt.State = stat;
                //log.Trace("torrent: name {0}; status {1}; progress {2}", ts.name, ts.state.ToString(), ts.progress);

                if (webServer != null)
                {
                    var context = Microsoft.AspNet.SignalR.GlobalHost.ConnectionManager.GetHubContext<www.SignalRHub>();
                    context.Clients.All.notifyUpdateProgress(evnt);
                }

                TorrentUpdated?.Invoke(null, evnt);
            }
        }

        private static void OnTorrentAddAlert(Core.torrent_added_alert a)
        {
            Core.TorrentHandle th = a.handle;
            if (TorrentHandles.TryAdd(th.info_hash().ToString(), th))
            {
                using (Core.TorrentStatus ts = th.status())
                {
                    var stat = "Paused";
                    if (!ts.paused)
                    {
                        stat = Utils.GiveMeStateFromEnum(ts.state);
                    }
                    var evnt = new EventsArgs.OnTorrentAddedEventArgs
                    {
                        Hash = th.info_hash().ToString(),
                        Name = ts.name,
                        Progress = ts.progress,
                        QueuePosition = ts.queue_position,
                        Status = stat
                    };
                    //log.Debug("torrent added: name {0}; status {1}; hash {2}", ts.name, ts.state.ToString(), ts.info_hash.ToString());

                    // notify web that a new id must be requested via webapi
                    if (webServer != null)
                    {
                        var context = Microsoft.AspNet.SignalR.GlobalHost.ConnectionManager.GetHubContext<www.SignalRHub>();
                        context.Clients.All.notifyTorrentAdded(evnt.Hash);
                    }

                    TorrentAdded?.Invoke(null, evnt);
                }
            }
        }

        private static void OnTorrentErrorAlert(Core.torrent_error_alert a)
        {
            var evnt = new Models.ErrorCode(a.error);

            //notify web
            if (webServer != null)
            {
                var context = Microsoft.AspNet.SignalR.GlobalHost.ConnectionManager.GetHubContext<www.SignalRHub>();
                context.Clients.All.notifyTorrentAdded(evnt);
            }

            TorrentError?.Invoke(null, evnt);
        }

        private static void OnStatsAlert(Core.stats_alert a)
        {

            int upload_payload = a.transferred[0];
            int upload_protocol = a.transferred[1];
            int download_payload = a.transferred[2];
            int download_protocol = a.transferred[3];
            int upload_ip_protocol = a.transferred[4];
            //int deprecated1 = a.transferred[5];
            //int deprecated2 = a.transferred[6];
            int download_ip_protocol = a.transferred[7];
            //int deprecated3 = a.transferred[8];
            //int deprecated4 = a.transferred[9];
            int num_channels = a.transferred[10];
        }

        public static void StreamTorrent(Core.TorrentHandle handle, int fileIndex)
        {
            var ti = handle.torrent_file();
            var files = ti.files();
            if(fileIndex < 0 || fileIndex > files.num_files())
            {
                throw new ArgumentOutOfRangeException();
            }
            var fileEntry = files.at(fileIndex);
            var peer_req = ti.map_file(fileIndex, 0, 1048576);
            var startPiece = peer_req.piece;
            var piece_length = ti.piece_length();
            var num_pieces = (int)Math.Ceiling((double)(fileEntry.size / piece_length));
            var end_piece = Math.Min(startPiece + num_pieces, ti.num_pieces() - 1);
            for (int i = startPiece; i < end_piece; i++)
                handle.piece_priority(i,0);
            
            //set first piece with higher priority
            handle.piece_priority(startPiece, 7);
            var lastPiece = startPiece;
            start_window = startPiece;
            end_window = Math.Min(end_window, lastPiece);
            for (int i = start_window; i <= end_window; i++)
                handle.piece_priority(i, 1);
            while (start_window <= end_window)
            {
                if (handle.have_piece(start_window))
                {
                    handle.piece_priority(++start_window, 7);
                    continue;
                }
                Thread.Sleep(200);
            }
        }
    }
}
