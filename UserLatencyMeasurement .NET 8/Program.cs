using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Media;
using System.Text.RegularExpressions;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections;
using System.Drawing;

using ThxLtd.Logging;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing;
using System.Collections.ObjectModel;
using System.IO;


namespace UserLatencyMeasurement_.NET_8
{
    public class SoundTestForm : System.Windows.Forms.Form
    {
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.TextBox filepathTextbox;
        private System.Windows.Forms.Button playOnceSyncButton;
        private System.Windows.Forms.Button playOnceAsyncButton;
        private System.Windows.Forms.Button playLoopAsyncButton;
        private System.Windows.Forms.Button selectFileButton;

        private System.Windows.Forms.Button stopButton;
        private System.Windows.Forms.ToolStripStatusLabel statusBar;
        private System.Windows.Forms.Button loadSyncButton;
        private System.Windows.Forms.Button loadAsyncButton;
        private System.Windows.Forms.DataGridView messageGrid;
        private SoundPlayer player;
        private readonly string _logTag = "THXUserLatencyMeasurement";
        private StatusStrip statusStrip;
        private DataGridViewTextBoxColumn TimeColumn;
        private DataGridViewTextBoxColumn TickCount64Column;
        private DataGridViewTextBoxColumn QPCColumn;
        private DataGridViewTextBoxColumn QPFColumn;
        private DataGridViewTextBoxColumn MessageColumn;
        private static readonly Guid _thxTraceLoggingProvider = Guid.Parse("f0b1069c-95da-4b61-b891-140fe9006e5d");

        private struct EventInfo
        {
            public string Message;
            public DateTime TimeStamp;
            public ulong TickCount64;
            public ulong QPC;
            public ulong QPF;
        }

        private class THXEventListener : EventListener
        {
            private readonly string _patternConstructorEnter = @"\(CVSOutAPOMFXBase\) - this = ([0-9A-Fa-f]+) - enter";
            private readonly string _patternDestructorExit = @"\(~CVSOutAPOMFXBase\) - this = ([0-9A-Fa-f]+) - exit";
            private readonly string _patternAPOProcessExit = @"\(APOProcess\) - this = ([0-9A-Fa-f]+) - exit";
            private readonly string _patternPlayAsyncClicked = @"\(playOnceAsyncButton_Click\) - this = [0-9A-Fa-f]+ - enter";
            private readonly string _patternPlaySyncClicked = @"\(playOnceSyncButton_Click\) - this = [0-9A-Fa-f]+ - enter";

            /// <summary>
            /// Place relevent events in this grid.
            /// </summary>
            private DataGridView _gridView;

            private ToolStripStatusLabel _statusLabel;

            /// <summary>
            ///  Map this pointer to constructor event. Items are removed when the destructor is detected.
            /// </summary>
            private readonly Dictionary<string, EventInfo> _events = new();

            private CancellationTokenSource _cts = new();

            /// <summary>
            /// Use an event queue to release the event listener thread as soon as possible.
            /// </summary>
            private BlockingCollection<EventInfo> _eventsQueue = new();

            /// <summary>
            /// File to which events are written. Accessed from ProcessEvent().
            /// </summary>
            private readonly StreamWriter _eventStreamWriter;

            private uint _eventCount = 0;

            public void Flush()
            {
                _eventStreamWriter.Flush();
            }

            public THXEventListener(StreamWriter eventStreamWriter, DataGridView gridView, ToolStripStatusLabel statusLabel)
            {
                _eventStreamWriter = eventStreamWriter;
                _gridView = gridView;
                _statusLabel = statusLabel;

                // Allow the event processing task to be cancelled.
                CancellationToken token = _cts.Token;

                ManualResetEventSlim mre = new ManualResetEventSlim(false);

                // Start a task to process events.
                _ = Task.Factory.StartNew(() =>
                {
                    System.Threading.Thread.CurrentThread.Name = "MessageProcessing";
                    System.Threading.Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

                    try
                    {
                        mre.Set();

                        // This enumerable will block until an event is available.
                        foreach (var ev in _eventsQueue.GetConsumingEnumerable())
                        {
                            try
                            {
                                ProcessEvent(ev);
                            }
                            catch (Exception ex)
                            {
                                _eventStreamWriter.WriteLine($"Event: {_eventCount} Depth: {_eventsQueue.Count} Exception: {ex.Message}");
                            }
                            finally
                            {
                                ++_eventCount;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // This is expected.
                    }
                }, token, TaskCreationOptions.LongRunning, TaskScheduler.Current);

                mre.Wait();
            }

            ~THXEventListener()
            {
                _eventsQueue.CompleteAdding();
            }

            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                base.OnEventSourceCreated(eventSource);
                if (eventSource.Guid.Equals(_thxTraceLoggingProvider))
                {
                    EnableEvents(eventSource, EventLevel.LogAlways, EventKeywords.All);
                }
                else if (eventSource.Name.Equals("THXDotNetLoggingProvider"))
                {
                    EnableEvents(eventSource, EventLevel.LogAlways, EventKeywords.All);
                }
            }

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                base.OnEventWritten(eventData);

                EventInfo eventInfo = new EventInfo()
                {
                    Message = eventData.Message,
                    TimeStamp = eventData.TimeStamp,
                };

                if (eventData.Payload != null)
                {
                    if (eventInfo.Message == null && eventData.Payload.Count > 0)
                    {
                        eventInfo.Message = eventData.Payload[0].ToString();
                    }

                    if (eventData.Payload.Count > 4)
                    {
                        eventInfo.TickCount64 = (ulong)eventData.Payload[4];
                    }
                    if (eventData.Payload.Count > 5)
                    {
                        eventInfo.QPC = (ulong)eventData.Payload[5];
                    }
                    if (eventData.Payload.Count > 6)
                    {
                        eventInfo.QPF = (ulong)eventData.Payload[6];
                    }
                }

                EnqueueEvent(eventInfo);
            }

            public void EnqueueEvent(EventInfo eventData)
            {
                _eventsQueue.Add(eventData);
            }

            private void ProcessEvent(EventInfo eventData)
            {
                _gridView.Invoke((MethodInvoker)delegate
                {
                    _statusLabel.Text = $"Event Queue Depth: {_eventsQueue.Count}";
                });
                _eventStreamWriter.WriteLine($"Event: {_eventCount} Depth: {_eventsQueue.Count} {eventData.TimeStamp.ToLocalTime().TimeOfDay} {eventData.Message}");

                string message = eventData.Message;

                Match match = Regex.Match(input: message, _patternConstructorEnter);
                if (match.Success)
                {
                    string thisPointer = match.Groups[1].Value;
                    _events.Add(thisPointer, eventData);
                    return;
                }

                match = Regex.Match(message, _patternDestructorExit);
                if (match.Success)
                {
                    string thisPointer = match.Groups[1].Value;
                    _events.Remove(thisPointer);
                    return;
                }

                match = Regex.Match(message, _patternAPOProcessExit);
                if (match.Success)
                {
                    string thisPointer = match.Groups[1].Value;
                    EventInfo constructorEnterEvent = _events[thisPointer];
                    _events.Remove(thisPointer);

                    _gridView.Invoke((MethodInvoker)delegate
                    {
                        _gridView.Rows.Add(constructorEnterEvent.TimeStamp.ToLocalTime().TimeOfDay, 
                            constructorEnterEvent.TickCount64,
                            constructorEnterEvent.QPC,
                            constructorEnterEvent.QPF,
                            "Windows instantiated THX");
                        _gridView.Rows.Add(eventData.TimeStamp.ToLocalTime().TimeOfDay, 
                            eventData.TickCount64,
                            eventData.QPC,
                            eventData.QPF,
                            "THX returns first audio to Windows.");
                    });
                    return;
                }

                match = Regex.Match(message, _patternPlayAsyncClicked);
                if (match.Success)
                {
                    _gridView.Invoke((MethodInvoker)delegate
                    {
                        _gridView.Rows.Add(eventData.TimeStamp.ToLocalTime().TimeOfDay, 
                            eventData.TickCount64,
                            eventData.QPC, 
                            eventData.QPF, 
                            "Playing Asynchronously");
                    });
                    return;
                }

                match = Regex.Match(message, _patternPlaySyncClicked);
                if (match.Success)
                {
                    _gridView.Invoke((MethodInvoker)delegate
                    {
                        _gridView.Rows.Add(eventData.TimeStamp.ToLocalTime().TimeOfDay, 
                            eventData.TickCount64,
                            eventData.QPC, 
                            eventData.QPF, 
                            "Playing Synchronously");
                    });
                    return;
                }
            }
        }

        private StreamWriter _eventStreamWriter = new(new FileStream(
                path: "THXUserLatencyMeasurement.log",
                mode: FileMode.Create,
                access: FileAccess.Write,
                share: FileShare.ReadWrite | FileShare.Delete));

        public SoundTestForm()
        {
            using (TraceLogging.THX_SCOPED_LOG_OBJECT(this, _logTag))
            {
                // Initialize Forms Designer generated code.
                InitializeComponent();

                // Disable playback controls until a valid .wav file 
                // is selected.
                EnablePlaybackControls(false);

                // Set up a Trace event session for receiving ETW events from the following providers:
                // f0b1069c-95da-4b61-b891-140fe9006e5d
                // THXDotNetLoggingProvider

                // Create a listener for the events.
                THXEventListener listener = new THXEventListener(_eventStreamWriter, gridView: this.messageGrid, statusLabel: statusBar);

                ManualResetEventSlim mre = new ManualResetEventSlim(false);

                _ = Task.Factory.StartNew(() =>
                {
                    // Set the name of this thread to "UserLatencyObserverTraceEventSession"
                    System.Threading.Thread.CurrentThread.Name = "UserLatencyObserverTraceEventSession";
                    System.Threading.Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

                    // Create a session to listen for events from the provider.
                    using (var session = new TraceEventSession("UserLatencyObserverTraceEventSession"))
                    {
#if true
                        session.BufferSizeMB = 2048;

                        session.EnableProvider(_thxTraceLoggingProvider);

                        session.Source.Dynamic.All += delegate (TraceEvent data)
                        {
                            if (data.ProviderGuid.Equals(_thxTraceLoggingProvider))
                            {
                                EventInfo eventInfo = new EventInfo()
                                {
                                    Message = data.FormattedMessage,
                                    TimeStamp = data.TimeStamp
                                };

                                if (data.PayloadNames != null)
                                {
                                    if (eventInfo.Message == null && data.PayloadNames.Length > 0)
                                    {
                                        eventInfo.Message = data.PayloadByName("Message").ToString();
                                    }

                                    if (data.PayloadNames.Length > 4)
                                    {
                                        eventInfo.TickCount64 = (ulong)(long)data.PayloadByName("TickCount64");
                                    }
                                    if (data.PayloadNames.Length > 5)
                                    {
                                        eventInfo.QPC = (ulong)(long)data.PayloadByName("QPC");
                                    }
                                    if (data.PayloadNames.Length > 6)
                                    {
                                        eventInfo.QPF = (ulong)(long)data.PayloadByName("QPF");
                                    }
                                }

                                listener.EnqueueEvent(eventInfo);
                            }
                        };
#endif
                        // Hold open the thread of the trace session until the user closes the form.
                        while (!IsDisposed)
                        {
                            mre.Set();

                            // Wait for events to be available.
                            session.Source.Process();
                        }
                    }
                }, TaskCreationOptions.LongRunning);

                mre.Wait();

                // Set up the status bar and other controls.
                InitializeControls();

                // Set up the SoundPlayer object.
                InitializeSound();
            }
        }

        // Sets up the status bar and other controls.
        private void InitializeControls()
        {
            using (TraceLogging.THX_SCOPED_LOG_OBJECT(this, _logTag))
            {
                this.statusBar.Text = "Ready. Select file using [..]";
            }
        }

        // Sets up the SoundPlayer object.
        private void InitializeSound()
        {
            using (TraceLogging.THX_SCOPED_LOG_OBJECT(this, _logTag))
            {
                // Create an instance of the SoundPlayer class.
                player = new SoundPlayer();

                // Listen for the LoadCompleted event.
                player.LoadCompleted += new AsyncCompletedEventHandler(player_LoadCompleted);

                // Listen for the SoundLocationChanged event.
                player.SoundLocationChanged += new EventHandler(player_LocationChanged);
            }
        }

        private void selectFileButton_Click(object sender,
            System.EventArgs e)
        {
            using (TraceLogging.THX_SCOPED_LOG_OBJECT(this, _logTag))
            {
                // Create a new OpenFileDialog.
                OpenFileDialog dlg = new OpenFileDialog();

                // Make sure the dialog checks for existence of the 
                // selected file.
                dlg.CheckFileExists = true;

                // Allow selection of .wav files only.
                dlg.Filter = "WAV files (*.wav)|*.wav";
                dlg.DefaultExt = ".wav";

                // Activate the file selection dialog.
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    // Get the selected file's path from the dialog.
                    this.filepathTextbox.Text = dlg.FileName;

                    // Assign the selected file's path to 
                    // the SoundPlayer object.  
                    player.SoundLocation = filepathTextbox.Text;
                }
            }
        }

        // Convenience method for setting message text in 
        // the status bar.
        private void ReportStatus(string statusMessage)
        {
            // If the caller passed in a message...
            if (!string.IsNullOrEmpty(statusMessage))
            {
                // ...post the caller's message to the status bar.
                this.statusBar.Text = statusMessage;
            }
        }

        // Enables and disables play controls.
        private void EnablePlaybackControls(bool enabled)
        {
            this.playOnceSyncButton.Enabled = enabled;
            this.playOnceAsyncButton.Enabled = enabled;
            this.playLoopAsyncButton.Enabled = enabled;
            this.stopButton.Enabled = enabled;
        }

        private void filepathTextbox_TextChanged(object sender,
            EventArgs e)
        {
            // Disable playback controls until the new .wav is loaded.
            EnablePlaybackControls(false);
        }

        private void loadSyncButton_Click(object sender,
            System.EventArgs e)
        {
            using (TraceLogging.THX_SCOPED_LOG_OBJECT(this, _logTag))
            {
                // Disable playback controls until the .wav is 
                // successfully loaded. The LoadCompleted event 
                // handler will enable them.
                EnablePlaybackControls(false);

                try
                {
                    // Assign the selected file's path to 
                    // the SoundPlayer object.  
                    player.SoundLocation = filepathTextbox.Text;

                    // Load the .wav file.
                    player.Load();
                }
                catch (Exception ex)
                {
                    ReportStatus(ex.Message);
                }
            }
        }

        private void loadAsyncButton_Click(System.Object sender,
            System.EventArgs e)
        {
            using (TraceLogging.THX_SCOPED_LOG_OBJECT(this, _logTag))
            {
                // Disable playback controls until the .wav is 
                // successfully loaded. The LoadCompleted event 
                // handler will enable them.
                EnablePlaybackControls(false);

                try
                {
                    // Assign the selected file's path to 
                    // the SoundPlayer object.  
                    player.SoundLocation = this.filepathTextbox.Text;

                    // Load the .wav file.
                    player.LoadAsync();
                }
                catch (Exception ex)
                {
                    ReportStatus(ex.Message);
                }
            }
        }

        // Synchronously plays the selected .wav file once.
        // If the file is large, UI response will be visibly 
        // affected.
        private void playOnceSyncButton_Click(object sender,
            System.EventArgs e)
        {
            using (TraceLogging.THX_SCOPED_LOG_OBJECT(this, _logTag))
            {
                ReportStatus("Playing .wav file synchronously.");
                player.PlaySync();
                ReportStatus("Finished playing .wav file synchronously.");
            }
        }

        // Asynchronously plays the selected .wav file once.
        private void playOnceAsyncButton_Click(object sender,
            System.EventArgs e)
        {
            using (TraceLogging.THX_SCOPED_LOG_OBJECT(this, _logTag))
            {
                ReportStatus("Playing .wav file asynchronously.");
                player.Play();
            }
        }

        // Asynchronously plays the selected .wav file until the user
        // clicks the Stop button.
        private void playLoopAsyncButton_Click(object sender,
            System.EventArgs e)
        {
            using (TraceLogging.THX_SCOPED_LOG_OBJECT(this, _logTag))
            {
                ReportStatus("Looping .wav file asynchronously.");
                player.PlayLooping();
            }
        }

        // Stops the currently playing .wav file, if any.
        private void stopButton_Click(System.Object sender,
            System.EventArgs e)
        {
            using (TraceLogging.THX_SCOPED_LOG_OBJECT(this, _logTag))
            {
                player.Stop();
                ReportStatus("Stopped by user.");
                _eventStreamWriter.Flush();
            }
        }

        // Handler for the LoadCompleted event.
        private void player_LoadCompleted(object sender,
            AsyncCompletedEventArgs e)
        {
            using (TraceLogging.THX_SCOPED_LOG_OBJECT(this, _logTag))
            {
                string message = String.Format("LoadCompleted: {0}",
                this.filepathTextbox.Text);
                ReportStatus(message);
                EnablePlaybackControls(true);
            }
        }

        // Handler for the SoundLocationChanged event.
        private void player_LocationChanged(object sender, EventArgs e)
        {
            string message = String.Format("SoundLocationChanged: {0}",
                player.SoundLocation);
            ReportStatus(message);
        }

        private void InitializeComponent()
        {
            filepathTextbox = new TextBox();
            selectFileButton = new Button();
            label1 = new Label();
            loadSyncButton = new Button();
            playOnceSyncButton = new Button();
            playOnceAsyncButton = new Button();
            stopButton = new Button();
            playLoopAsyncButton = new Button();
            statusBar = new ToolStripStatusLabel();
            statusStrip = new StatusStrip();
            loadAsyncButton = new Button();
            messageGrid = new DataGridView();
            TimeColumn = new DataGridViewTextBoxColumn();
            TickCount64Column = new DataGridViewTextBoxColumn();
            QPCColumn = new DataGridViewTextBoxColumn();
            QPFColumn = new DataGridViewTextBoxColumn();
            MessageColumn = new DataGridViewTextBoxColumn();
            statusStrip.SuspendLayout();
            ((ISupportInitialize)messageGrid).BeginInit();
            SuspendLayout();
            // 
            // filepathTextbox
            // 
            filepathTextbox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            filepathTextbox.Location = new Point(7, 25);
            filepathTextbox.Name = "filepathTextbox";
            filepathTextbox.Size = new Size(638, 23);
            filepathTextbox.TabIndex = 1;
            filepathTextbox.TextChanged += filepathTextbox_TextChanged;
            // 
            // selectFileButton
            // 
            selectFileButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            selectFileButton.Location = new Point(651, 25);
            selectFileButton.Name = "selectFileButton";
            selectFileButton.Size = new Size(23, 21);
            selectFileButton.TabIndex = 2;
            selectFileButton.Text = "...";
            selectFileButton.Click += selectFileButton_Click;
            // 
            // label1
            // 
            label1.Location = new Point(7, 7);
            label1.Name = "label1";
            label1.Size = new Size(145, 17);
            label1.TabIndex = 3;
            label1.Text = ".wav path or URL:";
            // 
            // loadSyncButton
            // 
            loadSyncButton.Location = new Point(7, 53);
            loadSyncButton.Name = "loadSyncButton";
            loadSyncButton.Size = new Size(142, 23);
            loadSyncButton.TabIndex = 4;
            loadSyncButton.Text = "Load Synchronously";
            loadSyncButton.Click += loadSyncButton_Click;
            // 
            // playOnceSyncButton
            // 
            playOnceSyncButton.Location = new Point(7, 86);
            playOnceSyncButton.Name = "playOnceSyncButton";
            playOnceSyncButton.Size = new Size(142, 23);
            playOnceSyncButton.TabIndex = 5;
            playOnceSyncButton.Text = "Play Synchronously";
            playOnceSyncButton.Click += playOnceSyncButton_Click;
            // 
            // playOnceAsyncButton
            // 
            playOnceAsyncButton.Location = new Point(149, 86);
            playOnceAsyncButton.Name = "playOnceAsyncButton";
            playOnceAsyncButton.Size = new Size(147, 23);
            playOnceAsyncButton.TabIndex = 6;
            playOnceAsyncButton.Text = "Play Asynchronously";
            playOnceAsyncButton.Click += playOnceAsyncButton_Click;
            // 
            // stopButton
            // 
            stopButton.Location = new Point(149, 109);
            stopButton.Name = "stopButton";
            stopButton.Size = new Size(147, 23);
            stopButton.TabIndex = 7;
            stopButton.Text = "Stop";
            stopButton.Click += stopButton_Click;
            // 
            // playLoopAsyncButton
            // 
            playLoopAsyncButton.Location = new Point(7, 109);
            playLoopAsyncButton.Name = "playLoopAsyncButton";
            playLoopAsyncButton.Size = new Size(142, 23);
            playLoopAsyncButton.TabIndex = 8;
            playLoopAsyncButton.Text = "Loop Asynchronously";
            playLoopAsyncButton.Click += playLoopAsyncButton_Click;
            // 
            // statusBar
            // 
            statusBar.Name = "statusBar";
            statusBar.Size = new Size(0, 17);
            // 
            // statusStrip
            // 
            statusStrip.Items.AddRange(new ToolStripItem[] { statusBar });
            statusStrip.Location = new Point(0, 299);
            statusStrip.Name = "statusStrip";
            statusStrip.Size = new Size(775, 22);
            statusStrip.SizingGrip = false;
            statusStrip.TabIndex = 9;
            // 
            // loadAsyncButton
            // 
            loadAsyncButton.Location = new Point(149, 53);
            loadAsyncButton.Name = "loadAsyncButton";
            loadAsyncButton.Size = new Size(147, 23);
            loadAsyncButton.TabIndex = 10;
            loadAsyncButton.Text = "Load Asynchronously";
            loadAsyncButton.Click += loadAsyncButton_Click;
            // 
            // messageGrid
            // 
            messageGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            messageGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            messageGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            messageGrid.Columns.AddRange(new DataGridViewColumn[] { TimeColumn, TickCount64Column, QPCColumn, QPFColumn, MessageColumn });
            messageGrid.EditMode = DataGridViewEditMode.EditProgrammatically;
            messageGrid.Location = new Point(30, 138);
            messageGrid.Name = "messageGrid";
            messageGrid.ReadOnly = true;
            messageGrid.Size = new Size(709, 150);
            messageGrid.TabIndex = 11;
            // 
            // TimeColumn
            // 
            TimeColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
            TimeColumn.HeaderText = "Time";
            TimeColumn.MinimumWidth = 20;
            TimeColumn.Name = "TimeColumn";
            TimeColumn.ReadOnly = true;
            TimeColumn.Width = 58;
            // 
            // TickCount64Column
            // 
            TickCount64Column.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
            TickCount64Column.HeaderText = "TickCount64";
            TickCount64Column.MinimumWidth = 20;
            TickCount64Column.Name = "TickCount64Column";
            TickCount64Column.ReadOnly = true;
            TickCount64Column.Width = 98;
            // 
            // QPCColumn
            // 
            QPCColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
            QPCColumn.HeaderText = "QPC";
            QPCColumn.MinimumWidth = 20;
            QPCColumn.Name = "QPCColumn";
            QPCColumn.ReadOnly = true;
            QPCColumn.Width = 56;
            // 
            // QPFColumn
            // 
            QPFColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
            QPFColumn.HeaderText = "QPF";
            QPFColumn.MinimumWidth = 20;
            QPFColumn.Name = "QPFColumn";
            QPFColumn.ReadOnly = true;
            QPFColumn.Width = 54;
            // 
            // MessageColumn
            // 
            MessageColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
            MessageColumn.HeaderText = "Message";
            MessageColumn.MinimumWidth = 400;
            MessageColumn.Name = "MessageColumn";
            MessageColumn.ReadOnly = true;
            MessageColumn.Width = 400;
            // 
            // SoundTestForm
            // 
            ClientSize = new Size(775, 321);
            Controls.Add(loadAsyncButton);
            Controls.Add(statusStrip);
            Controls.Add(playLoopAsyncButton);
            Controls.Add(stopButton);
            Controls.Add(playOnceAsyncButton);
            Controls.Add(playOnceSyncButton);
            Controls.Add(loadSyncButton);
            Controls.Add(label1);
            Controls.Add(selectFileButton);
            Controls.Add(filepathTextbox);
            Controls.Add(messageGrid);
            MinimumSize = new Size(400, 250);
            Name = "SoundTestForm";
            statusStrip.ResumeLayout(false);
            statusStrip.PerformLayout();
            ((ISupportInitialize)messageGrid).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new SoundTestForm());
        }
    }
}
