using NModbus;
using System.Net.Sockets;

namespace HMIProject
{
    public partial class Form1 : Form
    {
        // Connection settings (127.0.0.1 = simulator on this PC; use the PLC's IP for real hardware)
        private const string PlcIp = "127.0.0.1";
        private const int PlcPort = 502;
        private const byte SlaveId = 1;
        private const ushort PointCount = 10;
        private const int MaxLogLines = 500;

        private TcpClient? client;
        private IModbusMaster? master;

        private bool[] coilStates = Array.Empty<bool>();
        private bool[] inputStates = Array.Empty<bool>();
        private ushort[] inputRegisters = Array.Empty<ushort>();

        // Read types that are currently failing (so errors are logged once, not every poll)
        private readonly HashSet<string> failedReads = new();

        // Makes sure only one Modbus request is in flight at a time (timer vs. buttons)
        private readonly SemaphoreSlim ioLock = new(1, 1);
        private readonly System.Windows.Forms.Timer pollTimer = new() { Interval = 1000 };

        // Controls created in code so the designer file doesn't need editing
        private CheckBox chkPoll = null!;
        private NumericUpDown numInterval = null!;
        private ListBox lstLog = null!;

        public Form1()
        {
            InitializeComponent();
            BuildLiveControls();

            pollTimer.Tick += PollTimer_Tick;
            Load += Form1_Load;
            FormClosed += Form1_FormClosed;
        }

        // ---------- UI setup ----------

        private void BuildLiveControls()
        {
            int top = ClientSize.Height + 10;                      // below the existing controls
            ClientSize = new Size(Math.Max(ClientSize.Width, 420), top + 215);

            chkPoll = new CheckBox
            {
                Text = "Live polling",
                AutoSize = true,
                Location = new Point(12, top + 4)
            };
            chkPoll.CheckedChanged += ChkPoll_CheckedChanged;

            var lblInterval = new Label
            {
                Text = "Interval (ms):",
                AutoSize = true,
                Location = new Point(130, top + 6)
            };

            numInterval = new NumericUpDown
            {
                Minimum = 200,
                Maximum = 10000,
                Increment = 100,
                Value = 1000,
                Width = 70,
                Location = new Point(215, top + 2)
            };
            numInterval.ValueChanged += (s, e) => pollTimer.Interval = (int)numInterval.Value;

            var btnClearLog = new Button
            {
                Text = "Clear log",
                Width = 80,
                Location = new Point(300, top + 1)
            };
            btnClearLog.Click += (s, e) => lstLog.Items.Clear();

            lstLog = new ListBox
            {
                Location = new Point(12, top + 32),
                Size = new Size(ClientSize.Width - 24, 170),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Consolas", 9F),
                HorizontalScrollbar = true,
                IntegralHeight = false
            };

            Controls.AddRange(new Control[] { chkPoll, lblInterval, numInterval, btnClearLog, lstLog });
        }

        // ---------- Connection ----------

        private async void Form1_Load(object? sender, EventArgs e)
        {
            // Step 1: connect. Only a failure HERE means "not connected".
            try
            {
                client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(PlcIp, PlcPort, cts.Token);

                master = new ModbusFactory().CreateMaster(client);
                master.Transport.ReadTimeout = 2000;
                master.Transport.WriteTimeout = 2000;
                Log($"Connected to {PlcIp}:{PlcPort}");
            }
            catch (Exception ex)
            {
                master = null;
                Log($"ERROR connecting to {PlcIp}:{PlcPort} - {ex.Message}");
                MessageBox.Show($"Could not connect to {PlcIp}:{PlcPort}.\n{ex.Message}");
                return;
            }

            // Step 2: first read. A failure here (e.g. a point type not defined in the
            // simulator) is logged, but the connection stays usable.
            try
            {
                await RunExclusiveAsync(async () =>
                {
                    int succeeded = await ReadAllAsync(logChanges: false);
                    if (succeeded == 0)
                    {
                        Log("WARNING: no data could be read. Check the simulator's point ranges and Device Id.");
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"ERROR during first read - {ex.Message}");
            }
        }

        private void Form1_FormClosed(object? sender, FormClosedEventArgs e)
        {
            pollTimer.Stop();
            pollTimer.Dispose();
            client?.Dispose();
        }

        private bool IsConnected()
        {
            if (master == null)
            {
                MessageBox.Show("Not connected to the Modbus device. Restart the app after the simulator is running.");
                return false;
            }
            return true;
        }

        private async Task RunExclusiveAsync(Func<Task> action)
        {
            await ioLock.WaitAsync();
            try { await action(); }
            finally { ioLock.Release(); }
        }

        // ---------- Live polling ----------

        private void ChkPoll_CheckedChanged(object? sender, EventArgs e)
        {
            if (chkPoll.Checked)
            {
                if (master == null)
                {
                    Log("Cannot start polling: not connected");
                    chkPoll.Checked = false;
                    return;
                }
                pollTimer.Start();
                Log($"Polling started ({pollTimer.Interval} ms)");
            }
            else
            {
                pollTimer.Stop();
                Log("Polling stopped");
            }
        }

        private async void PollTimer_Tick(object? sender, EventArgs e)
        {
            if (master == null) return;

            // Skip this tick if a previous request is still running
            if (!await ioLock.WaitAsync(0)) return;
            try
            {
                int succeeded = await ReadAllAsync(logChanges: true);
                if (succeeded == 0)
                {
                    Log("Nothing could be read, stopping polling");
                    chkPoll.Checked = false;
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR polling - {ex.Message}");
                chkPoll.Checked = false;   // stops the timer without spamming message boxes
            }
            finally
            {
                ioLock.Release();
            }
        }

        // Reads one data type. Returns null on failure and logs it once until it recovers,
        // so one missing range in the simulator doesn't block the other reads.
        private async Task<T[]?> TryReadAsync<T>(string name, Func<Task<T[]>> read)
        {
            try
            {
                T[] result = await read();
                if (failedReads.Remove(name))
                {
                    Log($"OK {name} read recovered");
                }
                return result;
            }
            catch (Exception ex)
            {
                if (failedReads.Add(name))
                {
                    string reason = ex is SlaveException se
                        ? $"Modbus exception code {se.SlaveExceptionCode} (is a {name} range defined in the simulator?)"
                        : ex.Message;
                    Log($"ERROR reading {name} - {reason}");
                }
                return null;
            }
        }

        // Reads coils, discrete inputs and input registers and refreshes the UI.
        // Returns how many of the three reads succeeded.
        private async Task<int> ReadAllAsync(bool logChanges)
        {
            int succeeded = 0;

            bool[]? coils = await TryReadAsync("Coils", () => master!.ReadCoilsAsync(SlaveId, 0, PointCount));
            if (coils != null)
            {
                succeeded++;
                if (logChanges) LogChanges("Coil", coilStates, coils);
                coilStates = coils;
                FillCombo(comboBox2, "Coil", coils);
            }

            bool[]? inputs = await TryReadAsync("Discrete Inputs", () => master!.ReadInputsAsync(SlaveId, 0, PointCount));
            if (inputs != null)
            {
                succeeded++;
                if (logChanges) LogChanges("Input", inputStates, inputs);
                inputStates = inputs;
                FillCombo(comboBox1, "Input", inputs);
            }

            ushort[]? registers = await TryReadAsync("Input Registers", () => master!.ReadInputRegistersAsync(SlaveId, 0, PointCount));
            if (registers != null)
            {
                succeeded++;
                if (logChanges) LogChanges("Input Register", inputRegisters, registers);
                inputRegisters = registers;
                FillCombo(comboBox3, "Input Register", registers);
            }

            return succeeded;
        }

        // ---------- Button handlers ----------

        // Toggle the selected coil
        public async void button1_Click(object sender, EventArgs e)
        {
            if (!IsConnected()) return;

            int selectedIndex = comboBox2.SelectedIndex;
            if (selectedIndex < 0 || selectedIndex >= coilStates.Length)
            {
                MessageBox.Show("Select a coil to toggle");
                return;
            }

            try
            {
                await RunExclusiveAsync(async () =>
                {
                    bool newState = !coilStates[selectedIndex];

                    // Writes only the selected coil, not all 10
                    await master!.WriteSingleCoilAsync(SlaveId, (ushort)selectedIndex, newState);

                    coilStates[selectedIndex] = newState;
                    FillCombo(comboBox2, "Coil", coilStates);
                    comboBox2.SelectedIndex = selectedIndex;

                    Log($"WRITE Coil {selectedIndex} = {newState}");
                });
            }
            catch (Exception ex)
            {
                Log($"ERROR writing coil - {ex.Message}");
                MessageBox.Show($"Failed to write coil: {ex.Message}");
            }
        }

        // Write a value to holding register 0
        public async void button2_Click(object sender, EventArgs e)
        {
            if (!IsConnected()) return;

            if (!ushort.TryParse(textBox1.Text, out ushort value))
            {
                MessageBox.Show("Please enter a value between 0 and 65535");
                return;
            }

            try
            {
                await RunExclusiveAsync(async () =>
                {
                    await master!.WriteSingleRegisterAsync(SlaveId, 0, value);
                    Log($"WRITE Holding Register 0 = {value}");
                });
                MessageBox.Show("Register Successfully Updated");
            }
            catch (Exception ex)
            {
                Log($"ERROR writing register - {ex.Message}");
                MessageBox.Show($"Failed to write register: {ex.Message}");
            }
        }

        // Read discrete inputs once
        private async void button3_Click(object sender, EventArgs e)
        {
            if (!IsConnected()) return;

            try
            {
                await RunExclusiveAsync(async () =>
                {
                    inputStates = await master!.ReadInputsAsync(SlaveId, 0, PointCount);
                    FillCombo(comboBox1, "Input", inputStates);
                    Log($"READ Discrete Inputs 0-{PointCount - 1}");
                });
            }
            catch (Exception ex)
            {
                Log($"ERROR reading inputs - {ex.Message}");
                MessageBox.Show($"Failed to read inputs: {ex.Message}");
            }
        }

        // Read input registers once
        private async void button4_Click(object sender, EventArgs e)
        {
            if (!IsConnected()) return;

            try
            {
                await RunExclusiveAsync(async () =>
                {
                    inputRegisters = await master!.ReadInputRegistersAsync(SlaveId, 0, PointCount);
                    FillCombo(comboBox3, "Input Register", inputRegisters);
                    Log($"READ Input Registers 0-{PointCount - 1}");
                });
            }
            catch (Exception ex)
            {
                Log($"ERROR reading input registers - {ex.Message}");
                MessageBox.Show($"Failed to read input registers: {ex.Message}");
            }
        }

        // ---------- Helpers ----------

        // Refills a combo box but keeps the user's selection
        private static void FillCombo<T>(ComboBox box, string prefix, T[] values)
        {
            int selected = box.SelectedIndex;

            box.BeginUpdate();
            box.Items.Clear();
            for (int i = 0; i < values.Length; i++)
            {
                box.Items.Add($"{prefix} {i}: {values[i]}");
            }
            box.EndUpdate();

            if (box.Items.Count > 0)
            {
                box.SelectedIndex = (selected >= 0 && selected < box.Items.Count) ? selected : 0;
            }
        }

        // Logs only the values that changed since the last read, so the log doesn't flood
        private void LogChanges<T>(string name, T[] oldValues, T[] newValues)
        {
            if (oldValues.Length != newValues.Length) return;

            for (int i = 0; i < newValues.Length; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(oldValues[i], newValues[i]))
                {
                    Log($"CHANGE {name} {i}: {oldValues[i]} -> {newValues[i]}");
                }
            }
        }

        private void Log(string message)
        {
            lstLog.Items.Add($"{DateTime.Now:HH:mm:ss.fff}  {message}");

            while (lstLog.Items.Count > MaxLogLines)
            {
                lstLog.Items.RemoveAt(0);
            }
            lstLog.TopIndex = lstLog.Items.Count - 1;
        }
    }
}
