using grapher.Models.Calculations;
using grapher.Models.Devices;
using grapher.Models.Mouse;
using grapher.Models.Options;
using grapher.Models.Serialized;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace grapher
{
    public class AccelGUI
    {

        #region Constructors

        public AccelGUI(
            RawAcceleration accelForm,
            AccelCalculator accelCalculator,
            AccelCharts accelCharts,
            SettingsManager settings,
            ApplyOptions applyOptions,
            Button writeButton,
            Button resetButton,
            MouseWatcher mouseWatcher,
            ToolStripMenuItem scaleMenuItem,
            ToolStripMenuItem deviceMenuItem)
        {
            AccelForm = accelForm;
            AccelCalculator = accelCalculator;
            AccelCharts = accelCharts;
            ApplyOptions = applyOptions;
            WriteButton = writeButton;
            ResetButton = resetButton;
            ScaleMenuItem = scaleMenuItem;
            DeviceMenuItem = deviceMenuItem;
            Settings = settings;
            DefaultButtonFont = WriteButton.Font;
            SmallButtonFont = new Font(WriteButton.Font.Name, WriteButton.Font.Size * Constants.SmallButtonSizeFactor);
            MouseWatcher = mouseWatcher;

            DeviceMenuItem.Click += DeviceMenuItemClick;
            ScaleMenuItem.Click += new System.EventHandler(OnScaleMenuItemClick);
            WriteButton.Click += new System.EventHandler(OnWriteButtonClick);
            ResetButton.Click += new System.EventHandler(ResetDriverEventHandler);
            AccelForm.FormClosing += new FormClosingEventHandler(SaveGUISettingsOnClose);

            ButtonTimerInterval = Convert.ToInt32(DriverConfig.WriteDelayMs);
            ButtonTimer = new Timer();
            ButtonTimer.Tick += new System.EventHandler(OnButtonTimerTick);

            ChartRefresh = SetupChartTimer();

            RefreshUser();
            RefreshActive();
            SetupButtons();

            // TODO: The below removes an overlapping form from the anisotropy panel.
            // Figure out why and remove the overlap and below.
            ApplyOptions.Directionality.Show();
            ApplyOptions.Directionality.Hide();
        }

        #endregion Constructors

        #region Properties

        public RawAcceleration AccelForm { get; }

        public AccelCalculator AccelCalculator { get; }

        public AccelCharts AccelCharts { get; }

        public SettingsManager Settings { get; }

        public ApplyOptions ApplyOptions { get; }

        public Button WriteButton { get; }

        public Button ResetButton { get; }

        public Timer ButtonTimer { get; }

        public MouseWatcher MouseWatcher { get; }

        public ToolStripMenuItem ScaleMenuItem { get; }

        public ToolStripMenuItem DeviceMenuItem { get; }

        private Timer ChartRefresh { get; }

        private Font SmallButtonFont { get; }

        private Font DefaultButtonFont { get; }

        private bool SettingsNotDefault { get; set; }

        private bool LastToggleChecked { get; set; }

        private int ButtonTimerInterval { get; }

        private ToolStripMenuItem AccelToggleItem { get; set; }

        public bool AccelEnabled { get; private set; } = true;

        #endregion Properties

        #region Methods

        /// <summary>
        /// Adds an "Accel: ON/OFF" switch to the menu bar. OFF resets the driver to
        /// passthrough (no acceleration); ON reapplies the saved settings.
        /// </summary>
        public void AttachAccelToggle(MenuStrip menuStrip)
        {
            AccelToggleItem = new ToolStripMenuItem
            {
                Alignment = ToolStripItemAlignment.Right,
                Font = new Font(menuStrip.Font, FontStyle.Bold),
                ToolTipText = "Turn acceleration on or off without closing RawAccel",
            };
            AccelToggleItem.Click += (s, e) => SetAccelEnabled(!AccelEnabled);
            menuStrip.Items.Add(AccelToggleItem);
            UpdateAccelToggleItem();
        }

        /// <summary>
        /// Adds a "Settings File" menu: load a settings file now, and pick a file to
        /// auto-load (and apply) every time RawAccel starts.
        /// </summary>
        public void AttachSettingsFileMenu(MenuStrip menuStrip)
        {
            const string filter = "RawAccel settings (*.json)|*.json|All files (*.*)|*.*";

            var menu = new ToolStripMenuItem("Settings &File");

            var loadItem = new ToolStripMenuItem("&Load settings file...");
            loadItem.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = filter, Title = "Load RawAccel settings" })
                {
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        LoadSettingsFile(dlg.FileName);
                    }
                }
            };

            var autoLoadItem = new ToolStripMenuItem();
            var chooseAutoItem = new ToolStripMenuItem("&Choose file to auto-load...");
            var clearAutoItem = new ToolStripMenuItem("&Turn off auto-load");

            void RefreshAutoLoadItems()
            {
                var path = SettingsManager.AutoLoadPath;
                autoLoadItem.Checked = path != null;
                autoLoadItem.Text = path is null
                    ? "&Auto-load on startup: off"
                    : $"&Auto-load on startup: {System.IO.Path.GetFileName(path)}";
                autoLoadItem.ToolTipText = path ?? "No file is auto-loaded; settings.json is used as normal";
                clearAutoItem.Enabled = path != null;
            }

            chooseAutoItem.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = filter, Title = "Choose settings file to auto-load on startup" })
                {
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        SettingsManager.AutoLoadPath = dlg.FileName;
                        RefreshAutoLoadItems();
                        LoadSettingsFile(dlg.FileName);
                    }
                }
            };

            clearAutoItem.Click += (s, e) =>
            {
                SettingsManager.AutoLoadPath = null;
                RefreshAutoLoadItems();
            };

            autoLoadItem.DropDownItems.AddRange(new ToolStripItem[] { chooseAutoItem, clearAutoItem });
            menu.DropDownItems.AddRange(new ToolStripItem[] { loadItem, autoLoadItem });
            RefreshAutoLoadItems();

            // Put it before Help so the layout stays tidy.
            int helpIndex = menuStrip.Items.Count;
            for (int i = 0; i < menuStrip.Items.Count; i++)
            {
                if (menuStrip.Items[i].Text == "&Help") { helpIndex = i; break; }
            }
            menuStrip.Items.Insert(helpIndex, menu);
        }

        public void LoadSettingsFile(string path)
        {
            if (Settings.TryLoadFile(path, out string errors))
            {
                AccelEnabled = true;
                UpdateAccelToggleItem();
                RefreshUser();
                RefreshActive();
                Settings.SetActiveHandles();
            }
            else
            {
                using (var form = new MessageDialog(errors, "could not load settings file"))
                {
                    form.ShowDialog();
                }
            }
        }

        public void SetAccelEnabled(bool enabled)
        {
            if (enabled == AccelEnabled || (AccelToggleItem != null && !AccelToggleItem.Enabled)) return;

            var write = enabled ? Settings.TurnAccelOn() : Settings.TurnAccelOff();

            AccelEnabled = enabled;
            UpdateAccelToggleItem();

            if (enabled)
            {
                Settings.SetActiveHandles();
                UpdateGraph();
            }

            // The GUI fields are left alone either way, so your settings stay on screen.
            // Block clicks until the driver has finished this write.
            if (AccelToggleItem != null) AccelToggleItem.Enabled = false;

            write.ContinueWith(t =>
            {
                AccelForm.BeginInvoke((MethodInvoker)(() =>
                {
                    if (AccelToggleItem != null) AccelToggleItem.Enabled = true;

                    if (t.IsFaulted)
                    {
                        var msg = t.Exception?.GetBaseException().Message ?? "unknown error";
                        using (var form = new MessageDialog(msg, "could not change acceleration"))
                        {
                            form.ShowDialog();
                        }
                    }
                }));
            });
        }

        private void UpdateAccelToggleItem()
        {
            if (AccelToggleItem is null) return;

            AccelToggleItem.Text = AccelEnabled ? "Accel: ON" : "Accel: OFF";
            AccelToggleItem.ForeColor = AccelEnabled ? Color.ForestGreen : Color.Firebrick;
        }

        private void SaveGUISettingsOnClose(Object sender, FormClosingEventArgs e)
        {
            var guiSettings = Settings.MakeGUISettingsFromFields();
            if (!Settings.GuiSettings.Equals(guiSettings))
            {
                guiSettings.Save();
            }
        }

        public Profile MakeSettingsFromFields()
        {
            var settings = new Profile();

            settings.rotation = ApplyOptions.Rotation.Field.Data;
            settings.sensitivity = ApplyOptions.Sensitivity.Field.Data;

            // TODO - separate sensitivity fields, add new label for ratio
            settings.yxSensRatio = ApplyOptions.YToXRatio.Value;
            settings.combineMagnitudes = ApplyOptions.IsWhole;
            ApplyOptions.SetArgsFromActiveValues(ref settings.argsX, ref settings.argsY);

            var (domWeights, lpNorm) = ApplyOptions.Directionality.GetDomainArgs();
            settings.domainXY = domWeights;
            settings.lpNorm = lpNorm;

            settings.rangeXY = ApplyOptions.Directionality.GetRangeXY();

            Settings.SetHiddenOptions(settings);

            return settings;
        }

        public void UpdateActiveSettingsFromFields()
        {
            string error_message;

            try
            {
                ButtonDelay(WriteButton);

                if (!Settings.TryActivate(MakeSettingsFromFields(), out string errors))
                {
                    error_message = errors.ToString();
                }
                else
                {
                    AccelEnabled = true;
                    UpdateAccelToggleItem();
                    RefreshActive();
                    Settings.SetActiveHandles();
                    return;
                }
            }
            catch (ApplicationException e)
            {
                error_message = e.Message;
            }

            using (var form = new MessageDialog(error_message, "bad input"))
            {
                form.ShowDialog();
            }
        }

        public void RefreshActive()
        {
            UpdateShownActiveValues(Settings.ActiveProfile);
            UpdateGraph();
        }

        public void RefreshUser()
        {
            UpdateShownActiveValues(Settings.UserProfile);
        }

        public void UpdateGraph()
        {
            AccelCharts.Calculate(Settings.ActiveAccel, Settings.ActiveProfile);
            AccelCharts.Bind();
        }

        public void UpdateShownActiveValues(Profile args)
        {
            AccelForm.ResetAutoScroll();
            AccelCharts.ShowActive(args);
            ApplyOptions.SetActiveValues(args);
        }

        private Timer SetupChartTimer()
        {
            Timer chartTimer = new Timer();
            chartTimer.Enabled = true;
            chartTimer.Interval = 10;
            chartTimer.Tick += new System.EventHandler(OnChartTimerTick);
            return chartTimer;
        }

        private void SetupButtons()
        {
            WriteButton.Top = Constants.SensitivityChartAloneHeight - Constants.ButtonVerticalOffset;
            
            ResetButton.FlatStyle = FlatStyle.System;
            ResetButton.TextAlign = ContentAlignment.MiddleCenter;
            ResetButton.Size = WriteButton.Size;
            ResetButton.Top = WriteButton.Top;

            SetButtonDefaults();
        }

        private void SetButtonDefaults()
        {
            ResetButton.Font = DefaultButtonFont;
            ResetButton.Text = Constants.ResetButtonText;
            ResetButton.Enabled = true;
            ResetButton.Update();

            WriteButton.Font = DefaultButtonFont;
            WriteButton.Text = Constants.WriteButtonDefaultText;
            WriteButton.Enabled = true;
            WriteButton.Update();
        }

        private void OnScaleMenuItemClick(object sender, EventArgs e)
        {
            AccelCalculator.DPI.NewInputToData();
            AccelCalculator.PollRate.NewInputToData();
            UpdateGraph();
        }

        private void OnWriteButtonClick(object sender, EventArgs e)
        {
            UpdateActiveSettingsFromFields();
        }

        private void ResetDriverEventHandler(object sender, EventArgs e)
        {
            ButtonDelay(ResetButton);
            Settings.ResetDriver();
            AccelEnabled = false;
            UpdateAccelToggleItem();
            RefreshActive();
        }

        private void OnButtonTimerTick(object sender, EventArgs e)
        {
            ButtonTimer.Stop();
            SetButtonDefaults();
            DeviceMenuItem.Enabled = true;
        }

        private void StartButtonTimer()
        {
            DeviceMenuItem.Enabled = false;
            ButtonTimer.Interval = ButtonTimerInterval;
            ButtonTimer.Start();
        }

        private void ButtonDelay(ButtonBase btn)
        {
            ResetButton.Enabled = false;
            WriteButton.Enabled = false;

            btn.Font = SmallButtonFont;
            btn.Text = $"{Constants.ButtonDelayText} : {ButtonTimerInterval} ms";

            ResetButton.Update();
            WriteButton.Update();

            StartButtonTimer();
        }

        private void OnChartTimerTick(object sender, EventArgs e)
        {
            AccelCharts.DrawLastMovement();
            MouseWatcher.UpdateLastMove();
        }

        private void DeviceMenuItemClick(object sender, EventArgs e)
        {
            using (var devMenu = new DeviceMenuForm(Settings))
            {
                if (devMenu.ShowDialog() == DialogResult.OK)
                {
                    Settings.Submit(devMenu.defaultConfig, devMenu.Items);
                    UpdateActiveSettingsFromFields();
                }
            }
        }

        #endregion Methods
    }

}
