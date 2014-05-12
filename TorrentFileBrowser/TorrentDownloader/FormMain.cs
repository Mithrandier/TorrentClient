﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;
using BitTorrentProtocol;
using BitTorrentProtocol.Tracker;

namespace TorrentDownloader
{
    public partial class FormMain : Form
    {
        private Torrent torrent;
        private Client client;

        private String[] TRACKERS_COLUMN_HEADERS = new String[] { "Announce", "Status", "Complete", "Incomplete" };
        private int[] TRACKERS_COLUMN_WIDTHS = new int[] { 150, 97, 60, 70 };
        private const int FILE_SIZE_LIMIT = 128000;


        public FormMain()
        {
            InitializeComponent();
            dialogOpenTorrentFile.Filter = "Torrent files|*.torrent";
            client = new Client();
            InitTrackersTable();
            PrepareTimer();
            return;
        }

        /// <summary>
        /// Draw table for displaying trackers
        /// </summary>
        private void InitTrackersTable()
        {
            int columns_count = TRACKERS_COLUMN_HEADERS.Length; ;
            tableTrackers.Rows.Clear();
            tableTrackers.ColumnCount = columns_count;
            tableTrackers.RowHeadersVisible = false;
            for (int i = 0; i < columns_count; i++)
            {
                tableTrackers.Columns[i].HeaderText = TRACKERS_COLUMN_HEADERS[i];
                tableTrackers.Columns[i].Width = TRACKERS_COLUMN_WIDTHS[i];
            }
            return;
        }

        /// <summary>
        /// Initialize timer for updating progress info
        /// </summary>
        private void PrepareTimer()
        {
            timerDownloadProgress.Tick += new EventHandler(UpdateCurrentProgressRoutine);
            timerDownloadProgress.Interval = 1000;
            return;
        }

        /// <summary>
        /// Timer routine for updating GUI markers while downloading
        /// </summary>
        private void UpdateCurrentProgressRoutine(Object sender, EventArgs e)
        {
            lock (torrent)
            {
                UpdateProgressBar();
                if (torrent.Completed == 1.0)
                {
                    OnStopDownloading();
                    torrent.OnDownloadFinished();
                }
            }
            return;
        }

        private void UpdateProgressBar()
        {
            int pieces_total = torrent.PiecesCount;
            int downloaded = pieces_total - torrent.Bitfield.MissingPieces().Length;
            int percents_progress = downloaded * 100 / pieces_total;
            progressDownload.Value = percents_progress;
            labelProgress.Text = String.Format("{0}/{1}", downloaded, pieces_total);
            return;
        }


        /// <summary>
        /// Dialog for opening .torrent file
        /// </summary>
        private void openTorrentFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (dialogOpenTorrentFile.ShowDialog() == DialogResult.OK)
            {
                if (new FileInfo(dialogOpenTorrentFile.FileName).Length > FILE_SIZE_LIMIT)
                {
                    String message = String.Format("Sorry, selected file is too large. Try somethind less than {0}.", FormattedFileSize(FILE_SIZE_LIMIT));
                    MessageBox.Show(message);
                    return;
                }
                OnNewTorrentFile(dialogOpenTorrentFile.FileName);
            }
            return;
        }

        /// <summary>
        /// Display basic metafile info
        /// </summary>
        private void ShowTorrentFileInfo()
        {
            textTargetFileSize.Text = FormattedFileSize(torrent.Size);
            textDestinationFolder.Text = torrent.DownloadDirectory;
            dialogDestinationFolder.SelectedPath = torrent.DownloadDirectory;
            listFiles.Items.Clear();
            listFiles.Items.AddRange(torrent.Files.Select(f => FormattedFileInfo(f)).ToArray());
            return;
        }

        /// <summary>
        /// Get target file formatted for list view
        /// </summary>
        private String FormattedFileInfo(TargetFile file)
        {
            String name = Path.GetFileName(file.Path);
            return String.Format("{0} ({1})", name, FormattedFileSize(file.Size));
        }

        /// <summary>
        /// Get formatted file size (with measures)
        /// </summary>
        private String FormattedFileSize(long size)
        {
            String size_formatted = "0";
            int[] size_limits = new int[] { 1000 * 1000 * 1000, 1000 * 1000, 1000, 0 };
            String[] size_limita_abbr = new String[] { "GB", "MB", "KB", "B" };
            for (int i = 0; i < size_limits.Length; i++)
            {
                if (size > size_limits[i])
                {
                    double value = (size_limits[i] != 0 ? (double)size / size_limits[i] : size);
                    size_formatted = String.Format("{0} {1}", Math.Round(value, 2), size_limita_abbr[i]);
                    break;
                }
            }
            return size_formatted;
        }


        /// <summary>
        /// EXIT action
        /// </summary>
        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        /// <summary>
        /// START DOWNLOADING action
        /// </summary>
        private void startDownloadingToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (torrent == null) return;
            if (client.StartDownloading(torrent))
            {
                OnStartDownloading();
            }
            return;
        }

        /// <summary>
        /// UPDATE PEERS LIST action
        /// </summary>
        private void updateTrackingInfoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (torrent == null) return;
            OnUpdateTrackers();
            return;
        }

        /// <summary>
        /// ABOUT message
        /// </summary>
        private void aboutThisProgramToolStripMenuItem_Click(object sender, EventArgs e)
        {
            MessageBox.Show("Tracker downloader\n Written by Andrew Prostakov, 2014");
            return;
        }

        /// <summary>
        /// CHANGE DESTINATION action
        /// </summary>
        private void buttonChangeDestination_Click(object sender, EventArgs e)
        {
            if (dialogDestinationFolder.ShowDialog() == DialogResult.OK)
            {
                torrent.ChangeDownloadDirectory(dialogDestinationFolder.SelectedPath);
                textDestinationFolder.Text = dialogDestinationFolder.SelectedPath;
                UpdateProgressBar();
            }
            return;
        }

        /// <summary>
        /// STOP DOWNLOADING ACTION
        /// </summary>
        private void stopToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OnStopDownloading();
            return;
        }

        //
        // Complex logic & GUI behaviour
        //

        private void OnNewTorrentFile(String metafile_name)
        {
            textTorrentFileName.Text = Path.GetFileName(metafile_name);
            torrent = new Torrent(metafile_name);
            listPeers.Items.Clear();
            listPeers.Items.AddRange(torrent.PeersAddresses);
            tableTrackers.Rows.Clear();
            buttonChangeDestination.Enabled = true;
            updateTrackingInfoToolStripMenuItem.Enabled = true;
            startDownloadingToolStripMenuItem.Enabled = false;
            stopToolStripMenuItem.Enabled = false;
            ShowTorrentFileInfo();
            UpdateProgressBar();
            return;
        }

        private void OnUpdateTrackers()
        {
            client.CollectTorrentPeers(torrent);
            listPeers.Items.Clear();
            listPeers.Items.AddRange(torrent.PeersAddresses);
            tableTrackers.Rows.Clear();
            if (torrent.Announcers.Count > 0)
            {
                foreach (var tracker_info in torrent.Announcers.Values)
                {
                    ShowTrackerInfo(tracker_info);
                }
                startDownloadingToolStripMenuItem.Enabled = true;
                stopToolStripMenuItem.Enabled = false;
            }
            return;
        }

        /// <summary>
        /// Show new tracker in table
        /// </summary>
        private void ShowTrackerInfo(TorrentTrackerInfo tracker_info)
        {
            int row_index = tableTrackers.Rows.Add();
            for (int i = 0; i < TRACKERS_COLUMN_HEADERS.Length; i++)
            {
                tableTrackers.Rows[row_index].Cells[i].Value = tracker_info[TRACKERS_COLUMN_HEADERS[i]];
            }
            return;
        }

        private void OnStartDownloading()
        {
            startDownloadingToolStripMenuItem.Enabled = false;
            stopToolStripMenuItem.Enabled = true;
            updateTrackingInfoToolStripMenuItem.Enabled = false;
            buttonChangeDestination.Enabled = false;
            timerDownloadProgress.Start();
            openTorrentFileToolStripMenuItem.Enabled = false;
            return;
        }

        private void OnStopDownloading()
        {
            client.Pool.AbortAll();
            stopToolStripMenuItem.Enabled = false;
            startDownloadingToolStripMenuItem.Enabled = true;
            timerDownloadProgress.Stop();
            buttonChangeDestination.Enabled = true;
            updateTrackingInfoToolStripMenuItem.Enabled = true;
            openTorrentFileToolStripMenuItem.Enabled = true;
            return;
        }
    }
}
