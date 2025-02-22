﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Threading;
using System.Windows.Forms;
using BetterJoyForCemu.Controller;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace BetterJoyForCemu {
    public class Joycon {
        public enum ControllerType : int {
            JOYCON,
            PRO,
            SNES
        }

        public ControllerType type = ControllerType.JOYCON;

        public bool isPro {
            get {
                return (type == ControllerType.PRO || type == ControllerType.SNES);
            }
        }
        public bool isSnes {
            get {
                return (type == ControllerType.SNES);
            }
        }
        public bool isThirdParty = false;
        public bool isUSB = false;
        public string path = String.Empty;
        
        private Joycon _other = null;
        public Joycon other {
            get {
                return _other;
            }
            set {
                _other = value;

                // If the other Joycon is itself, the Joycon is sideways
                if (_other == null || _other == this) {
                    // Set LED to current Pad ID
                    SetLEDByPlayerNum(PadId);
                } else {
                    // Set LED to current Joycon Pair
                    int lowestPadId = Math.Min(_other.PadId, PadId);
                    SetLEDByPlayerNum(lowestPadId);
                }
            }
        }
        public bool active_gyro = false;

        private long inactivity = Stopwatch.GetTimestamp();

        public bool send = true;

        public enum DebugType : int {
            NONE,
            ALL,
            COMMS,
            THREADING,
            IMU,
            RUMBLE,
            SHAKE,
        };
        public DebugType debug_type = (DebugType)int.Parse(ConfigurationManager.AppSettings["DebugType"]);
        //public DebugType debug_type = DebugType.NONE; //Keep this for manual debugging during development.
        public bool isLeft;
        public enum state_ : uint {
            NOT_ATTACHED,
            DROPPED,
            NO_JOYCONS,
            ATTACHED,
            INPUT_MODE_0x30,
            IMU_DATA_OK,
        };
        public state_ state;
        public enum Button : int {
            DPAD_DOWN = 0,
            DPAD_RIGHT = 1,
            DPAD_LEFT = 2,
            DPAD_UP = 3,
            SL = 4,
            SR = 5,
            MINUS = 6,
            HOME = 7,
            PLUS = 8,
            CAPTURE = 9,
            STICK = 10,
            SHOULDER_1 = 11,
            SHOULDER_2 = 12,

            // For pro controller
            B = 13,
            A = 14,
            Y = 15,
            X = 16,
            STICK2 = 17,
            SHOULDER2_1 = 18,
            SHOULDER2_2 = 19,
        };
        private bool[] buttons_down = new bool[20];
        private bool[] buttons_up = new bool[20];
        private bool[] buttons = new bool[20];
        private bool[] down_ = new bool[20];
        private long[] buttons_down_timestamp = new long[20];

        private float[] stick = { 0, 0 };
        private float[] stick2 = { 0, 0 };

        private IntPtr handle;

        byte[] default_buf = { 0x0, 0x1, 0x40, 0x40, 0x0, 0x1, 0x40, 0x40 };

        private byte[] stick_raw = { 0, 0, 0 };
        private UInt16[] stick_cal = { 0, 0, 0, 0, 0, 0 };
        private UInt16 deadzone = 0;
        private UInt16[] stick_precal = { 0, 0 };

        private byte[] stick2_raw = { 0, 0, 0 };
        private UInt16[] stick2_cal = { 0, 0, 0, 0, 0, 0 };
        private UInt16 deadzone2 = 0;
        private UInt16[] stick2_precal = { 0, 0 };

        private bool stop_polling = true;
        private bool imu_enabled = false;
        private Int16[] acc_r = { 0, 0, 0 };
        private Int16[] acc_neutral = { 0, 0, 0 };
        private Int16[] acc_sensiti = { 0, 0, 0 };
        private Vector3 acc_g = Vector3.Zero;

        private Int16[] gyr_r = { 0, 0, 0 };
        private Int16[] gyr_neutral = { 0, 0, 0 };
        private Int16[] gyr_sensiti = { 0, 0, 0 };
        private Vector3 gyr_g = Vector3.Zero;

        private float[] cur_rotation; // Filtered IMU data

        private short[] acc_sen = new short[3]{
            16384,
            16384,
            16384
        };
        private short[] gyr_sen = new short[3]{
            18642,
            18642,
            18642
        };

        private Int16[] pro_hor_offset = { -710, 0, 0 };
        private Int16[] left_hor_offset = { 0, 0, 0 };
        private Int16[] right_hor_offset = { 0, 0, 0 };

        private bool do_localize;
        private float filterweight;
        private const int report_len = 49;

        private struct Rumble {
            private Queue<float[]> queue;
            private SpinLock queueLock;

            public void set_vals(float low_freq, float high_freq, float amplitude) {
                float[] rumbleQueue = new float[] { low_freq, high_freq, amplitude };
                // Keep a queue of 15 items, discard oldest item if queue is full.
                bool lockTaken = false;
                try {
                    queueLock.Enter(ref lockTaken);
                    if (queue.Count > 15) {
                        queue.Dequeue();
                    }
                    queue.Enqueue(rumbleQueue);
                } finally {
                    if (lockTaken) {
                        queueLock.Exit();
                    }
                }
            }
            public Rumble(float[] rumble_info) {
                queue = new Queue<float[]>();
                queueLock = new SpinLock();
                queue.Enqueue(rumble_info);
            }
            private float clamp(float x, float min, float max) {
                if (x < min) return min;
                if (x > max) return max;
                return x;
            }

            private byte EncodeAmp(float amp) {
                byte en_amp;

                if (amp == 0)
                    en_amp = 0;
                else if (amp < 0.117)
                    en_amp = (byte)(((Math.Log(amp * 1000, 2) * 32) - 0x60) / (5 - Math.Pow(amp, 2)) - 1);
                else if (amp < 0.23)
                    en_amp = (byte)(((Math.Log(amp * 1000, 2) * 32) - 0x60) - 0x5c);
                else
                    en_amp = (byte)((((Math.Log(amp * 1000, 2) * 32) - 0x60) * 2) - 0xf6);

                return en_amp;
            }

            public byte[] GetData() {
                float[] queued_data = null;
                bool lockTaken = false;
                try {
                    queueLock.Enter(ref lockTaken);
                    if (queue.Count > 0) {
                        queued_data = queue.Dequeue();
                    }
                } finally {
                    if (lockTaken) {
                        queueLock.Exit();
                    }
                }
                if (queued_data == null) {
                    return null;
                }
                byte[] rumble_data = new byte[8];

                if (queued_data[2] == 0.0f) {
                    rumble_data[0] = 0x0;
                    rumble_data[1] = 0x1;
                    rumble_data[2] = 0x40;
                    rumble_data[3] = 0x40;
                } else {
                    queued_data[0] = clamp(queued_data[0], 40.875885f, 626.286133f);
                    queued_data[1] = clamp(queued_data[1], 81.75177f, 1252.572266f);

                    queued_data[2] = clamp(queued_data[2], 0.0f, 1.0f);

                    UInt16 hf = (UInt16)((Math.Round(32f * Math.Log(queued_data[1] * 0.1f, 2)) - 0x60) * 4);
                    byte lf = (byte)(Math.Round(32f * Math.Log(queued_data[0] * 0.1f, 2)) - 0x40);
                    byte hf_amp = EncodeAmp(queued_data[2]);

                    UInt16 lf_amp = (UInt16)(Math.Round((double)hf_amp) * .5);
                    byte parity = (byte)(lf_amp % 2);
                    if (parity > 0) {
                        --lf_amp;
                    }

                    lf_amp = (UInt16)(lf_amp >> 1);
                    lf_amp += 0x40;
                    if (parity > 0) lf_amp |= 0x8000;

                    hf_amp = (byte)(hf_amp - (hf_amp % 2)); // make even at all times to prevent weird hum
                    rumble_data[0] = (byte)(hf & 0xff);
                    rumble_data[1] = (byte)(((hf >> 8) & 0xff) + hf_amp);
                    rumble_data[2] = (byte)(((lf_amp >> 8) & 0xff) + lf);
                    rumble_data[3] = (byte)(lf_amp & 0xff);
                }

                for (int i = 0; i < 4; ++i) {
                    rumble_data[4 + i] = rumble_data[i];
                }

                return rumble_data;
            }
        }

        private Rumble rumble_obj;

        private byte global_count = 0;

        // For UdpServer
        public int PadId = 0;
        public int battery = -1;
        public int model = 2;
        public int constate = 2;
        public int connection = 3;

        public PhysicalAddress PadMacAddress = new PhysicalAddress(new byte[] { 01, 02, 03, 04, 05, 06 });
        public ulong Timestamp = 0;
        public int packetCounter = 0;

        public OutputControllerXbox360 out_xbox;
        public OutputControllerDualShock4 out_ds4;

        static int lowFreq = Int32.Parse(ConfigurationManager.AppSettings["LowFreqRumble"]);
        static int highFreq = Int32.Parse(ConfigurationManager.AppSettings["HighFreqRumble"]);

        static bool toRumble = Boolean.Parse(ConfigurationManager.AppSettings["EnableRumble"]);

        static bool showAsXInput = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsXInput"]);
        static bool showAsDS4 = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsDS4"]);

        static bool useIncrementalLights = Boolean.Parse(ConfigurationManager.AppSettings["UseIncrementalLights"]);

        public MainForm form;

        public byte LED { get; private set; } = 0x0;
        public void SetLEDByPlayerNum(int id) {
            if (id > 3) {
                // No support for any higher than 3 (4 Joycons/Controllers supported in the application normally)
                id = 3;
            }

            if (useIncrementalLights) {
                // Set all LEDs from 0 to the given id to lit
                int ledId = id;
                LED = 0x0;
                do {
                    LED |= (byte)(0x1 << ledId);
                } while (--ledId >= 0);
            } else {
                LED = (byte)(0x1 << id);
            }

            SetPlayerLED(LED);
        }

        public string serial_number;

        private float[] activeIMUData;
        private ushort[] activeStick1Data;
        private ushort[] activeStick2Data;
        private ushort[] noCalibrationSticksData;
        private ushort activeStick1DeadZoneData;
        private ushort activeStick2DeadZoneData;
        private ushort noCalibrationDeadzone;
        static private float defaultDeadzone = float.Parse(ConfigurationManager.AppSettings["SticksDeadzone"]);
        static private float AHRS_beta = float.Parse(ConfigurationManager.AppSettings["AHRS_beta"]);
        private MadgwickAHRS AHRS = new MadgwickAHRS(0.005f, AHRS_beta); // for getting filtered Euler angles of rotation; 5ms sampling rate

        public Joycon(IntPtr handle_, bool imu, bool localize, float alpha, bool left, string path, string serialNum, bool isUSB, int id = 0, ControllerType type = ControllerType.JOYCON, bool isThirdParty = false) {
            serial_number = serialNum;
            activeIMUData = new float[6];
            activeStick1Data = new ushort[6];
            activeStick2Data = new ushort[6];
            noCalibrationSticksData = new ushort[6] { 2048, 2048, 2048, 2048, 2048, 2048 };
            noCalibrationDeadzone = calculateDeadzone(noCalibrationSticksData, defaultDeadzone);
            handle = handle_;
            imu_enabled = imu;
            do_localize = localize;
            rumble_obj = new Rumble(new float[] { lowFreq, highFreq, 0 });
            for (int i = 0; i < buttons_down_timestamp.Length; i++)
                buttons_down_timestamp[i] = -1;
            filterweight = alpha;
            isLeft = left;

            PadId = id;
            LED = (byte)(0x1 << PadId);

            this.isUSB = isUSB;
            this.type = type;
            this.isThirdParty = isThirdParty;
            this.path = path;

            connection = isUSB ? 0x01 : 0x02;

            if (showAsXInput) {
                out_xbox = new OutputControllerXbox360();
                if (toRumble)
                    out_xbox.FeedbackReceived += ReceiveRumble;
            }

            if (showAsDS4) {
                out_ds4 = new OutputControllerDualShock4();
                if (toRumble)
                    out_ds4.FeedbackReceived += Ds4_FeedbackReceived;
            }
        }

        public void getActiveIMUData() {
            this.activeIMUData = form.activeCaliIMUData(serial_number);
        }
        public void getActiveSticksData() {
            {
                ushort[] activeSticksData = form.activeCaliSticksData(serial_number);
                Array.Copy(activeSticksData, this.activeStick1Data, 6);
                Array.Copy(activeSticksData, 6, this.activeStick2Data, 0, 6);
            }
            this.activeStick1DeadZoneData = calculateDeadzone(activeStick1Data, defaultDeadzone);
            this.activeStick2DeadZoneData = calculateDeadzone(activeStick2Data, defaultDeadzone);
        }

        public ushort calculateDeadzone(ushort[] stickDatas, float deadzone) {
            ushort deadzone1 = (ushort) Math.Round(Math.Abs(stickDatas[0] + stickDatas[4]) * deadzone);
            ushort deadzone2 = (ushort) Math.Round(Math.Abs(stickDatas[1] + stickDatas[5]) * deadzone);

            return Math.Max(deadzone1, deadzone2);
        }
        public void ReceiveRumble(Xbox360FeedbackReceivedEventArgs e) {
            DebugPrint("Rumble data Received: XInput", DebugType.RUMBLE);
            SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);

            if (other != null && other != this)
                other.SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);
        }

        public void Ds4_FeedbackReceived(DualShock4FeedbackReceivedEventArgs e) {
            DebugPrint("Rumble data Received: DS4", DebugType.RUMBLE);
            SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);

            if (other != null && other != this)
                other.SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);
        }

        public void DebugPrint(String s, DebugType d) {
            if (debug_type == DebugType.NONE) return;
            if (d == DebugType.ALL || d == debug_type || debug_type == DebugType.ALL) {
                form.AppendTextBox("[J" + (PadId + 1) + "] " + s + "\r\n");
            }
        }
        public bool GetButtonDown(Button b) {
            return buttons_down[(int)b];
        }
        public bool GetButton(Button b) {
            return buttons[(int)b];
        }
        public bool GetButtonUp(Button b) {
            return buttons_up[(int)b];
        }
        public float[] GetStick() {
            return stick;
        }
        public float[] GetStick2() {
            return stick2;
        }
        public Vector3 GetGyro() {
            return gyr_g;
        }
        public Vector3 GetAccel() {
            return acc_g;
        }
        public void Reset() {
            form.AppendTextBox("Resetting connection.\r\n");
            SetHCIState(0x01);
        }
        public int Attach() {
            state = state_.ATTACHED;

            // set report mode to simple HID mode (fix SPI read not working when controller is already initialized)
            // do not always send a response so we don't check if there is one
            Subcommand(0x3, new byte[] { 0x3F }, 1);

            // Connect
            if (isUSB) {
                form.AppendTextBox("Using USB.\r\n");

                var buf = new byte[report_len];

                // Get MAC
                buf[0] = 0x80; buf[1] = 0x1;
                HIDapi.hid_write(handle, buf, new UIntPtr(2));
                if (ReadUSBCheck(buf, 0x1) == 0) { // can occur when USB connection isn't closed properly
                    Reset();
                    throw new Exception("reset mac");
                }

                if (buf[3] == 0x3) {
                    PadMacAddress = new PhysicalAddress(new byte[] { buf[9], buf[8], buf[7], buf[6], buf[5], buf[4] });
                }

                // USB Pairing
                buf[0] = 0x80; buf[1] = 0x2; // Handshake
                HIDapi.hid_write(handle, buf, new UIntPtr(2));
                if (ReadUSBCheck(buf, 0x2) == 0) {
                    // can occur when another software sends commands to the device, disable PurgeAffectedDevice in the config to avoid this
                    Reset();
                    throw new Exception("reset handshake");
                }

                buf[0] = 0x80; buf[1] = 0x3; // 3Mbit baud rate
                HIDapi.hid_write(handle, buf, new UIntPtr(2));
                if (ReadUSBCheck(buf, 0x3) == 0) {
                    Reset();
                    throw new Exception("reset baud rate");
                }

                buf[0] = 0x80; buf[1] = 0x2; // Handshake at new baud rate
                HIDapi.hid_write(handle, buf, new UIntPtr(2));
                if (ReadUSBCheck(buf, 0x2) == 0) {
                    Reset();
                    throw new Exception("reset new handshake");
                }

                buf[0] = 0x80; buf[1] = 0x4; // Prevent HID timeout
                HIDapi.hid_write(handle, buf, new UIntPtr(2)); // does not send a response

            } else {
                form.AppendTextBox("Using Bluetooth.\r\n");
            }

            bool ok = dump_calibration_data();
            if (!ok) {
                Reset();
                throw new Exception("reset calibration");
            }

            // Bluetooth manual pairing
            //byte[] btmac_host = Program.btMAC.GetAddressBytes();
            // send host MAC and acquire Joycon MAC
            //byte[] reply = Subcommand(0x01, new byte[] { 0x01, btmac_host[5], btmac_host[4], btmac_host[3], btmac_host[2], btmac_host[1], btmac_host[0] }, 7, true);
            //byte[] LTKhash = Subcommand(0x01, new byte[] { 0x02 }, 1, true);
            // save pairing info
            //Subcommand(0x01, new byte[] { 0x03 }, 1, true);

            BlinkHomeLight();
            SetLEDByPlayerNum(PadId);

            Subcommand(0x40, new byte[] { (imu_enabled ? (byte)0x1 : (byte)0x0) }, 1); // enable IMU
            Subcommand(0x48, new byte[] { 0x01 }, 1); // enable vibrations
            Subcommand(0x3, new byte[] { 0x30 }, 1); // set report mode to NPad standard mode

            DebugPrint("Done with init.", DebugType.COMMS);

            return 0;
        }

        public void SetPlayerLED(byte leds_ = 0x0) {
            Subcommand(0x30, new byte[] { leds_ }, 1);
        }

        public void BlinkHomeLight() { // do not call after initial setup
            if (isThirdParty)
                return;

            byte[] buf = Enumerable.Repeat((byte)0xFF, 25).ToArray();
            buf[0] = 0x18;
            buf[1] = 0x01;
            Subcommand(0x38, buf, 25);
        }

        public void SetHomeLight(bool on) {
            if (isThirdParty)
                return;

            byte[] buf = Enumerable.Repeat((byte)0xFF, 25).ToArray();
            if (on) {
                buf[0] = 0x1F;
                buf[1] = 0xF0;
            } else {
                buf[0] = 0x10;
                buf[1] = 0x01;
            }
            Subcommand(0x38, buf, 25);
        }

        private void SetHCIState(byte state) {
            Subcommand(0x06, new byte[] { state }, 1);
        }

        public void PowerOff() {
            if (state > state_.DROPPED) {
                SetHCIState(0x00);
                state = state_.DROPPED;
            }
        }

        private void BatteryChanged() { // battery changed level
            form.setBatteryColor(this, battery);

            if (!isUSB && battery <= 1) {
                string msg = String.Format("Controller {0} ({1}) - low battery notification!", PadId, getControllerName());
                form.tooltip(msg);
            }
        }

        public void SetFilterCoeff(float a) {
            filterweight = a;
        }

        public void Detach(bool close = true) {
            stop_polling = true;
            if (PollThreadObj != null) {
                PollThreadObj.Join();
            }

            DisconnectViGEm();

            if (state > state_.NO_JOYCONS) {
                // Subcommand(0x40, new byte[] { 0x0 }, 1); // disable IMU sensor
                //Subcommand(0x48, new byte[] { 0x0 }, 1); // Would turn off rumble?

                if (isUSB) {
                    // Commented because you need to restart the controller to reconnect in usb again with the following
                    //var buf = new byte[report_len];
                    //buf[0] = 0x80; buf[1] = 0x5; // Allow device to talk to BT again
                    //HIDapi.hid_write(handle, buf, new UIntPtr(2));
                    //ReadUSBCheck(Buffer, 0x5);
                    //buf[0] = 0x80; buf[1] = 0x6; // Allow device to talk to BT again
                    //HIDapi.hid_write(handle, buf, new UIntPtr(2));
                    //ReadUSBCheck(Buffer, 0x6);
                }
            }
            if (close && handle != IntPtr.Zero) {
                HIDapi.hid_close(handle);
                handle = IntPtr.Zero;
            }
            state = state_.NOT_ATTACHED;
        }

        public void Drop()
        {
            stop_polling = true;
            if (PollThreadObj != null) {
                PollThreadObj.Join();
            }
            state = state_.DROPPED;
        }

        public void ConnectViGEm()
        {
            if (out_xbox != null) {
                out_xbox.Connect();
            }
            if (out_ds4 != null) {
                out_ds4.Connect();
            }
        }

        public void DisconnectViGEm()
        {
            try {
                if (out_xbox != null) {
                    out_xbox.Disconnect();
                }
                if (out_ds4 != null) {
                    out_ds4.Disconnect();
                }
            } catch (Exception /*e*/) {
                // nothing we can do, might not be connected in the first place
            }
            out_xbox = null;
            out_ds4 = null;
        }

        private byte ts_en;
        private enum ReceiveError {
            None,
            InvalidHandle,
            ReadError,
            InvalidPacket,
            NoData
        };

        // Run from poll thread
        private ReceiveError ReceiveRaw(byte[] buf) {
            if (handle == IntPtr.Zero) {
                return ReceiveError.InvalidHandle;
            }
            int length = HIDapi.hid_read_timeout(handle, buf, new UIntPtr(report_len), 5);
            if (length < 0) {
                return ReceiveError.ReadError;
            }
            if (length == 0) {
                return ReceiveError.NoData;
            }
            if (buf[0] != 0x30) { // 0x30 = standard full mode report
                return ReceiveError.InvalidPacket;
            }

            // clear remaining of buffer just to be safe
            if (length < report_len) {
                Array.Clear(buf, length, report_len - length);
            }

            // Process packets as soon as they come
            for (int n = 0; n < 3; n++) {
                ExtractIMUValues(buf, n);

                if (n == 0) {
                    byte lag = (byte)Math.Max(0, buf[1] - ts_en - 3); // why -3 ?
                    Timestamp += (ulong)lag * 5000; // add lag once
                    ProcessButtonsAndStick(buf);

                    DoThingsWithButtons();

                    int prevBattery = battery;
                    battery = (buf[2] >> 4) / 2;
                    if (prevBattery != battery)
                        BatteryChanged();
                }
                Timestamp += 5000; // 5ms difference

                packetCounter++;
                Program.server?.NewReportIncoming(this);
            }

            if (out_ds4 != null) {
                try {
                    out_ds4.UpdateInput(MapToDualShock4Input(this));
                } catch (Exception /*e*/) {
                    // ignore /shrug
                }
            }
            if (out_xbox != null) {
                try {
                    out_xbox.UpdateInput(MapToXbox360Input(this));
                } catch (Exception /*e*/) {
                    // ignore /shrug
                }
            }

            if (ts_en == buf[1] && !isSnes) {
                form.AppendTextBox("Duplicate timestamp enqueued.\r\n");
                DebugPrint(string.Format("Duplicate timestamp enqueued. TS: {0:X2}", ts_en), DebugType.THREADING);
            }
            ts_en = buf[1];
            //DebugPrint(string.Format("Enqueue. Bytes read: {0:D}. Type: {1:X2} Timestamp: {2:X2}", length, buf[0], buf[1]), DebugType.THREADING);
            
            return ReceiveError.None;
        }

        private readonly Stopwatch shakeTimer = Stopwatch.StartNew(); //Setup a timer for measuring shake in milliseconds
        private long shakedTime = 0;
        private bool hasShaked;
        void DetectShake() {
            if (form.shakeInputEnabled) {
                long currentShakeTime = shakeTimer.ElapsedMilliseconds;

                // Shake detection logic
                bool isShaking = GetAccel().LengthSquared() >= form.shakeSesitivity;
                if (isShaking && currentShakeTime >= shakedTime + form.shakeDelay || isShaking && shakedTime == 0) {
                    shakedTime = currentShakeTime;
                    hasShaked = true;

                    // Mapped shake key down
                    Simulate(Config.Value("shake"), false, false);
                    DebugPrint("Shaked at time: " + shakedTime.ToString(), DebugType.SHAKE);
                }

                // If controller was shaked then release mapped key after a small delay to simulate a button press, then reset hasShaked
                if (hasShaked && currentShakeTime >= shakedTime + 10) {
                    // Mapped shake key up
                    Simulate(Config.Value("shake"), false, true);
                    DebugPrint("Shake completed", DebugType.SHAKE);
                    hasShaked = false;
                }

            } else {
                shakeTimer.Stop();
                return;
            }
        }

        bool dragToggle = Boolean.Parse(ConfigurationManager.AppSettings["DragToggle"]);
        Dictionary<int, bool> mouse_toggle_btn = new Dictionary<int, bool>();
        private void Simulate(string s, bool click = true, bool up = false) {
            if (s.StartsWith("key_")) {
                WindowsInput.Events.KeyCode key = (WindowsInput.Events.KeyCode)Int32.Parse(s.Substring(4));
                if (click) {
                    WindowsInput.Simulate.Events().Click(key).Invoke();
                } else {
                    if (up) {
                        WindowsInput.Simulate.Events().Release(key).Invoke();
                    } else {
                        WindowsInput.Simulate.Events().Hold(key).Invoke();
                    }
                }
            } else if (s.StartsWith("mse_")) {
                WindowsInput.Events.ButtonCode button = (WindowsInput.Events.ButtonCode)Int32.Parse(s.Substring(4));
                if (click) {
                    WindowsInput.Simulate.Events().Click(button).Invoke();
                } else {
                    if (dragToggle) {
                        if (!up) {
                            bool release;
                            mouse_toggle_btn.TryGetValue((int)button, out release);
                            if (release)
                                WindowsInput.Simulate.Events().Release(button).Invoke();
                            else
                                WindowsInput.Simulate.Events().Hold(button).Invoke();
                            mouse_toggle_btn[(int)button] = !release;
                        }
                    } else {
                        if (up) {
                            WindowsInput.Simulate.Events().Release(button).Invoke();
                        } else {
                            WindowsInput.Simulate.Events().Hold(button).Invoke();
                        }
                    }
                }
            }
        }

        // For Joystick->Joystick inputs
        private void SimulateContinous(int origin, string s) {
            if (s.StartsWith("joy_")) {
                int button = Int32.Parse(s.Substring(4));
                buttons[button] |= buttons[origin];
            }
        }

        bool HomeLongPowerOff = Boolean.Parse(ConfigurationManager.AppSettings["HomeLongPowerOff"]);
        long PowerOffInactivityMins = Int32.Parse(ConfigurationManager.AppSettings["PowerOffInactivity"]);

        bool ChangeOrientationDoubleClick = Boolean.Parse(ConfigurationManager.AppSettings["ChangeOrientationDoubleClick"]);
        long lastDoubleClick = -1;

        string extraGyroFeature = ConfigurationManager.AppSettings["GyroToJoyOrMouse"];
        bool UseFilteredIMU = Boolean.Parse(ConfigurationManager.AppSettings["UseFilteredIMU"]);
        bool GyroMouseLeftHanded = Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseLeftHanded"]);
        int GyroMouseSensitivityX = Int32.Parse(ConfigurationManager.AppSettings["GyroMouseSensitivityX"]);
        int GyroMouseSensitivityY = Int32.Parse(ConfigurationManager.AppSettings["GyroMouseSensitivityY"]);
        float GyroStickSensitivityX = float.Parse(ConfigurationManager.AppSettings["GyroStickSensitivityX"]);
        float GyroStickSensitivityY = float.Parse(ConfigurationManager.AppSettings["GyroStickSensitivityY"]);
        float GyroStickReduction = float.Parse(ConfigurationManager.AppSettings["GyroStickReduction"]);
        bool GyroHoldToggle = Boolean.Parse(ConfigurationManager.AppSettings["GyroHoldToggle"]);
        bool GyroAnalogSliders = Boolean.Parse(ConfigurationManager.AppSettings["GyroAnalogSliders"]);
        int GyroAnalogSensitivity = Int32.Parse(ConfigurationManager.AppSettings["GyroAnalogSensitivity"]);
        byte[] sliderVal = new byte[] { 0, 0 };

        private void DoThingsWithButtons() {
            int powerOffButton = (int)((isPro || !isLeft || other != null) ? Button.HOME : Button.CAPTURE);

            long timestamp = Stopwatch.GetTimestamp();
            if (HomeLongPowerOff && buttons[powerOffButton]) {
                if ((timestamp - buttons_down_timestamp[powerOffButton]) / 10000 > 2000.0) {
                    if (other != null)
                        other.PowerOff();

                    PowerOff();
                    return;
                }
            }

            if(!isPro) {
                if (ChangeOrientationDoubleClick && buttons_down[(int)Button.STICK] && lastDoubleClick != -1) {
                    if ((buttons_down_timestamp[(int)Button.STICK] - lastDoubleClick) < 3000000) {
                        form.conBtnClick(PadId); // trigger connection button click

                        lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
                        return;
                    }
                    lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
                } else if (ChangeOrientationDoubleClick && buttons_down[(int)Button.STICK]) {
                    lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
                }
            }

            if (PowerOffInactivityMins > 0) {
                if ((timestamp - inactivity) / 10000 > PowerOffInactivityMins * 60 * 1000) {
                    if (other != null)
                        other.PowerOff();

                    PowerOff();
                    return;
                }
            }

            DetectShake();

            if (buttons_down[(int)Button.CAPTURE])
                Simulate(Config.Value("capture"), false, false);
            if (buttons_up[(int)Button.CAPTURE])
                Simulate(Config.Value("capture"), false, true);
            if (buttons_down[(int)Button.HOME])
                Simulate(Config.Value("home"), false, false);
            if (buttons_up[(int)Button.HOME])
                Simulate(Config.Value("home"), false, true);
            SimulateContinous((int)Button.CAPTURE, Config.Value("capture"));
            SimulateContinous((int)Button.HOME, Config.Value("home"));

            if (isLeft) {
                if (buttons_down[(int)Button.SL])
                    Simulate(Config.Value("sl_l"), false, false);
                if (buttons_up[(int)Button.SL])
                    Simulate(Config.Value("sl_l"), false, true);
                if (buttons_down[(int)Button.SR])
                    Simulate(Config.Value("sr_l"), false, false);
                if (buttons_up[(int)Button.SR])
                    Simulate(Config.Value("sr_l"), false, true);

                SimulateContinous((int)Button.SL, Config.Value("sl_l"));
                SimulateContinous((int)Button.SR, Config.Value("sr_l"));
            } else {
                if (buttons_down[(int)Button.SL])
                    Simulate(Config.Value("sl_r"), false, false);
                if (buttons_up[(int)Button.SL])
                    Simulate(Config.Value("sl_r"), false, true);
                if (buttons_down[(int)Button.SR])
                    Simulate(Config.Value("sr_r"), false, false);
                if (buttons_up[(int)Button.SR])
                    Simulate(Config.Value("sr_r"), false, true);

                SimulateContinous((int)Button.SL, Config.Value("sl_r"));
                SimulateContinous((int)Button.SR, Config.Value("sr_r"));
            }

            // Filtered IMU data
            this.cur_rotation = AHRS.GetEulerAngles();
            const float dt = 0.015f; // 15ms

            if (GyroAnalogSliders && (other != null || isPro)) {
                Button leftT = isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2;
                Button rightT = isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2;
                Joycon left = (isLeft || isPro) ? this : this.other;
                Joycon right = (!isLeft || isPro) ? this : this.other;

                int ldy, rdy;
                if (UseFilteredIMU) {
                    ldy = (int)(GyroAnalogSensitivity * (left.cur_rotation[0] - left.cur_rotation[3]));
                    rdy = (int)(GyroAnalogSensitivity * (right.cur_rotation[0] - right.cur_rotation[3]));
                } else {
                    ldy = (int)(GyroAnalogSensitivity * (left.gyr_g.Y * dt));
                    rdy = (int)(GyroAnalogSensitivity * (right.gyr_g.Y * dt));
                }

                if (buttons[(int)leftT]) {
                    sliderVal[0] = (byte)Math.Min(Byte.MaxValue, Math.Max(0, (int)sliderVal[0] + ldy));
                } else {
                    sliderVal[0] = 0;
                }

                if (buttons[(int)rightT]) {
                    sliderVal[1] = (byte)Math.Min(Byte.MaxValue, Math.Max(0, (int)sliderVal[1] + rdy));
                } else {
                    sliderVal[1] = 0;
                }
            }

            string res_val = Config.Value("active_gyro");
            if (res_val.StartsWith("joy_")) {
                int i = Int32.Parse(res_val.Substring(4));
                if (GyroHoldToggle) {
                    if (buttons_down[i] || (other != null && other.buttons_down[i]))
                        active_gyro = true;
                    else if (buttons_up[i] || (other != null && other.buttons_up[i]))
                        active_gyro = false;
                } else {
                    if (buttons_down[i] || (other != null && other.buttons_down[i]))
                        active_gyro = !active_gyro;
                }
            }

            if (extraGyroFeature.Substring(0, 3) == "joy") {
                if (Config.Value("active_gyro") == "0" || active_gyro) {
                    float[] control_stick = (extraGyroFeature == "joy_left") ? stick : stick2;

                    float dx, dy;
                    if (UseFilteredIMU) {
                        dx = (GyroStickSensitivityX * (cur_rotation[1] - cur_rotation[4])); // yaw
                        dy = -(GyroStickSensitivityY * (cur_rotation[0] - cur_rotation[3])); // pitch
                    } else {
                        dx = (GyroStickSensitivityX * (gyr_g.Z * dt)); // yaw
                        dy = -(GyroStickSensitivityY * (gyr_g.Y * dt)); // pitch
                    }

                    control_stick[0] = Math.Max(-1.0f, Math.Min(1.0f, control_stick[0] / GyroStickReduction + dx));
                    control_stick[1] = Math.Max(-1.0f, Math.Min(1.0f, control_stick[1] / GyroStickReduction + dy));
                }
            } else if (extraGyroFeature == "mouse" && (isPro || (other == null) || (other != null && (GyroMouseLeftHanded ? isLeft : !isLeft)))) {
                // gyro data is in degrees/s
                if (Config.Value("active_gyro") == "0" || active_gyro) {
                    int dx, dy;

                    if (UseFilteredIMU) {
                        dx = (int)(GyroMouseSensitivityX * (cur_rotation[1] - cur_rotation[4])); // yaw
                        dy = (int)-(GyroMouseSensitivityY * (cur_rotation[0] - cur_rotation[3])); // pitch
                    } else {
                        dx = (int)(GyroMouseSensitivityX * (gyr_g.Z * dt));
                        dy = (int)-(GyroMouseSensitivityY * (gyr_g.Y * dt));
                    }

                    WindowsInput.Simulate.Events().MoveBy(dx, dy).Invoke();
                }

                // reset mouse position to centre of primary monitor
                res_val = Config.Value("reset_mouse");
                if (res_val.StartsWith("joy_")) {
                    int i = Int32.Parse(res_val.Substring(4));
                    if (buttons_down[i] || (other != null && other.buttons_down[i]))
                        WindowsInput.Simulate.Events().MoveTo(Screen.PrimaryScreen.Bounds.Width / 2, Screen.PrimaryScreen.Bounds.Height / 2).Invoke();
                }
            }
        }

        private Thread PollThreadObj;
        private void Poll() {
            byte[] buf = new byte[report_len];
            stop_polling = false;
            int attempts = 0;
            while (!stop_polling && state > state_.NO_JOYCONS) {
                {
                    byte[] data = rumble_obj.GetData();
                    if (data != null) {
                        SendRumble(buf, data);
                    }
                }
                ReceiveError error = ReceiveRaw(buf);

                if (error == ReceiveError.None && state > state_.DROPPED) {
                    state = state_.IMU_DATA_OK;
                    attempts = 0;
                } else if (attempts > 240) {
                    state = state_.DROPPED;
                    form.AppendTextBox("Dropped.\r\n");
                    DebugPrint("Connection lost. Is the Joy-Con connected?", DebugType.ALL);
                } else if (error == ReceiveError.InvalidHandle) { // should not happen
                    state = state_.DROPPED;
                    form.AppendTextBox("Dropped (invalid handle).\r\n");
                } else {
                    // No data read, read error or invalid packet
                    // The controller should report back at 60hz or 120hz for the pro controller
                    if (error == ReceiveError.ReadError) {
                        Thread.Sleep(5);
                    }
                    ++attempts;
                }
            }
        }

        public float[] otherStick = { 0, 0 };

        bool swapAB = Boolean.Parse(ConfigurationManager.AppSettings["SwapAB"]);
        bool swapXY = Boolean.Parse(ConfigurationManager.AppSettings["SwapXY"]);
        private int ProcessButtonsAndStick(byte[] report_buf) {
            if (!isSnes) {
                int reportOffset = (isLeft ? 0 : 3);
                stick_raw[0] = report_buf[6 + reportOffset];
                stick_raw[1] = report_buf[7 + reportOffset];
                stick_raw[2] = report_buf[8 + reportOffset];

                if (isPro) {
                    reportOffset = (!isLeft ? 0 : 3);
                    stick2_raw[0] = report_buf[6 + reportOffset];
                    stick2_raw[1] = report_buf[7 + reportOffset];
                    stick2_raw[2] = report_buf[8 + reportOffset];
                }

                stick_precal[0] = (UInt16)(stick_raw[0] | ((stick_raw[1] & 0xf) << 8));
                stick_precal[1] = (UInt16)((stick_raw[1] >> 4) | (stick_raw[2] << 4));
                ushort[] cal = stick_cal;
                ushort dz = deadzone;
                if (form.allowCalibration) {
                    cal = activeStick1Data;
                    dz = activeStick1DeadZoneData;
                    if (form.calibrateSticks) {
                        form.xS1.Add(stick_precal[0]);
                        form.yS1.Add(stick_precal[1]);
                    }
                }
                else if (!form.useControllerStickCalibration) {
                    cal = noCalibrationSticksData;
                    dz = noCalibrationDeadzone;
                }
                stick = CenterSticks(stick_precal, cal, dz);

                if (isPro) {
                    stick2_precal[0] = (UInt16)(stick2_raw[0] | ((stick2_raw[1] & 0xf) << 8));
                    stick2_precal[1] = (UInt16)((stick2_raw[1] >> 4) | (stick2_raw[2] << 4));
                    if (form.allowCalibration) {
                        cal = activeStick2Data;
                        dz = activeStick2DeadZoneData;
                        if (form.calibrateSticks) {
                            form.xS2.Add(stick2_precal[0]);
                            form.yS2.Add(stick2_precal[1]);
                        }
                    }
                    else if (form.useControllerStickCalibration) {
                        cal = stick2_cal;
                        dz = deadzone2;
                    }
                    stick2 = CenterSticks(stick2_precal, cal, dz);
                }
                if (!form.calibrateSticks) {
                    DebugPrint(string.Format("Stick1: X={0} Y={1}. Stick2: X={2} Y={3}", stick[0], stick[1], stick2[0], stick2[1]), DebugType.THREADING);
                }

                // Read other Joycon's sticks
                if (other != null && other != this) {
                    if (isLeft) {
                        stick2 = otherStick;
                        other.otherStick = stick;
                    }
                    else {
                        Array.Copy(stick, stick2, 2);
                        stick = otherStick;
                        other.otherStick = stick2;
                    }
                }
            }

            // Set button states both for server and ViGEm
            lock (buttons) {
                lock (down_) {
                    for (int i = 0; i < buttons.Length; ++i) {
                        down_[i] = buttons[i];
                    }
                }
                buttons = new bool[20];
                int reportOffset = (isLeft ? 2 : 0);

                buttons[(int)Button.DPAD_DOWN] = (report_buf[3 + reportOffset] & (isLeft ? 0x01 : 0x04)) != 0;
                buttons[(int)Button.DPAD_RIGHT] = (report_buf[3 + reportOffset] & (isLeft ? 0x04 : 0x08)) != 0;
                buttons[(int)Button.DPAD_UP] = (report_buf[3 + reportOffset] & (isLeft ? 0x02 : 0x02)) != 0;
                buttons[(int)Button.DPAD_LEFT] = (report_buf[3 + reportOffset] & (isLeft ? 0x08 : 0x01)) != 0;
                buttons[(int)Button.HOME] = ((report_buf[4] & 0x10) != 0);
                buttons[(int)Button.CAPTURE] = ((report_buf[4] & 0x20) != 0);
                buttons[(int)Button.MINUS] = ((report_buf[4] & 0x01) != 0);
                buttons[(int)Button.PLUS] = ((report_buf[4] & 0x02) != 0);
                buttons[(int)Button.STICK] = ((report_buf[4] & (isLeft ? 0x08 : 0x04)) != 0);
                buttons[(int)Button.SHOULDER_1] = (report_buf[3 + reportOffset] & 0x40) != 0;
                buttons[(int)Button.SHOULDER_2] = (report_buf[3 + reportOffset] & 0x80) != 0;
                buttons[(int)Button.SR] = (report_buf[3 + reportOffset] & 0x10) != 0;
                buttons[(int)Button.SL] = (report_buf[3 + reportOffset] & 0x20) != 0;

                if (isPro) {
                    reportOffset = (!isLeft ? 2 : 0);

                    buttons[(int)Button.B] = (report_buf[3 + reportOffset] & (!isLeft ? 0x01 : 0x04)) != 0;
                    buttons[(int)Button.A] = (report_buf[3 + reportOffset] & (!isLeft ? 0x04 : 0x08)) != 0;
                    buttons[(int)Button.X] = (report_buf[3 + reportOffset] & (!isLeft ? 0x02 : 0x02)) != 0;
                    buttons[(int)Button.Y] = (report_buf[3 + reportOffset] & (!isLeft ? 0x08 : 0x01)) != 0;

                    buttons[(int)Button.STICK2] = ((report_buf[4] & (!isLeft ? 0x08 : 0x04)) != 0);
                    buttons[(int)Button.SHOULDER2_1] = (report_buf[3 + reportOffset] & 0x40) != 0;
                    buttons[(int)Button.SHOULDER2_2] = (report_buf[3 + reportOffset] & 0x80) != 0;
                }

                if (other != null && other != this) {
                    buttons[(int)(Button.B)] = other.buttons[(int)Button.DPAD_DOWN];
                    buttons[(int)(Button.A)] = other.buttons[(int)Button.DPAD_RIGHT];
                    buttons[(int)(Button.X)] = other.buttons[(int)Button.DPAD_UP];
                    buttons[(int)(Button.Y)] = other.buttons[(int)Button.DPAD_LEFT];

                    buttons[(int)Button.STICK2] = other.buttons[(int)Button.STICK];
                    buttons[(int)Button.SHOULDER2_1] = other.buttons[(int)Button.SHOULDER_1];
                    buttons[(int)Button.SHOULDER2_2] = other.buttons[(int)Button.SHOULDER_2];

                    if (isLeft) {
                        buttons[(int)Button.HOME] = other.buttons[(int)Button.HOME];
                        buttons[(int)Button.PLUS] = other.buttons[(int)Button.PLUS];
                    }
                    else {
                        buttons[(int)Button.CAPTURE] = other.buttons[(int)Button.CAPTURE];
                        buttons[(int)Button.MINUS] = other.buttons[(int)Button.MINUS];
                    }
                }

                long timestamp = Stopwatch.GetTimestamp();

                lock (buttons_up) {
                    lock (buttons_down) {
                        bool changed = false;
                        for (int i = 0; i < buttons.Length; ++i) {
                            buttons_up[i] = (down_[i] & !buttons[i]);
                            buttons_down[i] = (!down_[i] & buttons[i]);
                            if (down_[i] != buttons[i])
                                buttons_down_timestamp[i] = (buttons[i] ? timestamp : -1);
                            if (buttons_up[i] || buttons_down[i])
                                changed = true;
                        }

                        inactivity = (changed) ? timestamp : inactivity;
                    }
                }
            }

            return 0;
        }

        // Get Gyro/Accel data
        private void ExtractIMUValues(byte[] report_buf, int n = 0) {
            if (!isSnes) {
                gyr_r[0] = (Int16)(report_buf[19 + n * 12] | ((report_buf[20 + n * 12] << 8) & 0xff00));
                gyr_r[1] = (Int16)(report_buf[21 + n * 12] | ((report_buf[22 + n * 12] << 8) & 0xff00));
                gyr_r[2] = (Int16)(report_buf[23 + n * 12] | ((report_buf[24 + n * 12] << 8) & 0xff00));
                acc_r[0] = (Int16)(report_buf[13 + n * 12] | ((report_buf[14 + n * 12] << 8) & 0xff00));
                acc_r[1] = (Int16)(report_buf[15 + n * 12] | ((report_buf[16 + n * 12] << 8) & 0xff00));
                acc_r[2] = (Int16)(report_buf[17 + n * 12] | ((report_buf[18 + n * 12] << 8) & 0xff00));

                int direction = (isLeft ? 1 : -1);

                if (form.allowCalibration) {
                    acc_g.X = (acc_r[0] - activeIMUData[3]) * (1.0f / acc_sen[0]) * 4.0f;
                    gyr_g.X = (gyr_r[0] - activeIMUData[0]) * (816.0f / gyr_sen[0]);
                    if (form.calibrateIMU) {
                        form.xA.Add(acc_r[0]);
                        form.xG.Add(gyr_r[0]);
                    }

                    acc_g.Y = direction * (acc_r[1] - activeIMUData[4]) * (1.0f / acc_sen[1]) * 4.0f;
                    gyr_g.Y = -direction * (gyr_r[1] - activeIMUData[1]) * (816.0f / gyr_sen[1]);
                    if (form.calibrateIMU) {
                        form.yA.Add(acc_r[1]);
                        form.yG.Add(gyr_r[1]);
                    }

                    acc_g.Z = direction * (acc_r[2] - activeIMUData[5]) * (1.0f / acc_sen[2]) * 4.0f;
                    gyr_g.Z = -direction * (gyr_r[2] - activeIMUData[2]) * (816.0f / gyr_sen[2]);
                    if (form.calibrateIMU) {
                        form.zA.Add(acc_r[2]);
                        form.zG.Add(gyr_r[2]);
                    }
                } else {
                    Int16[] offset;
                    if (isPro)
                        offset = pro_hor_offset;
                    else if (isLeft)
                        offset = left_hor_offset;
                    else
                        offset = right_hor_offset;

                    acc_g.X = (acc_r[0] - offset[0]) * (1.0f / (acc_sensiti[0] - acc_neutral[0])) * 4.0f;
                    gyr_g.X = (gyr_r[0] - gyr_neutral[0]) * (816.0f / (gyr_sensiti[0] - gyr_neutral[0]));

                    acc_g.Y = direction * (acc_r[1] - offset[1]) * (1.0f / (acc_sensiti[1] - acc_neutral[1])) * 4.0f;
                    gyr_g.Y = -direction * (gyr_r[1] - gyr_neutral[1]) * (816.0f / (gyr_sensiti[1] - gyr_neutral[1]));

                    acc_g.Z = direction * (acc_r[2] - offset[2]) * (1.0f / (acc_sensiti[2] - acc_neutral[2])) * 4.0f;
                    gyr_g.Z = -direction * (gyr_r[2] - gyr_neutral[2]) * (816.0f / (gyr_sensiti[2] - gyr_neutral[2]));
                }

                if (!isPro && other == null) { // single joycon mode; Z do not swap, rest do
                    if (isLeft) {
                        acc_g.X = -acc_g.X;
                        acc_g.Y = -acc_g.Y;
                        gyr_g.X = -gyr_g.X;
                    } else {
                        gyr_g.Y = -gyr_g.Y;
                    }

                    float temp = acc_g.X;
                    acc_g.X = acc_g.Y;
                    acc_g.Y = -temp;

                    temp = gyr_g.X;
                    gyr_g.X = gyr_g.Y;
                    gyr_g.Y = temp;
                }

                // Update rotation Quaternion
                float deg_to_rad = 0.0174533f;
                AHRS.Update(gyr_g.X * deg_to_rad, gyr_g.Y * deg_to_rad, gyr_g.Z * deg_to_rad, acc_g.X, acc_g.Y, acc_g.Z);
            }
        }

        public void Begin() {
            if (PollThreadObj == null) {
                PollThreadObj = new Thread(new ThreadStart(Poll));
                PollThreadObj.IsBackground = true;
                PollThreadObj.Start();

                form.AppendTextBox("Starting poll thread.\r\n");
            } else {
                form.AppendTextBox("Poll cannot start.\r\n");
            }
        }

        // Should really be called calculating stick data
        private float[] CenterSticks(UInt16[] vals, ushort[] cal, ushort dz) {
            float[] s = { 0, 0 };
            float dx = vals[0] - cal[2], dy = vals[1] - cal[3];
            if (Math.Abs(dx * dx + dy * dy) < dz * dz)
                return s;

            s[0] = dx / (dx > 0 ? cal[0] : cal[4]);
            s[1] = dy / (dy > 0 ? cal[1] : cal[5]);
            return s;
        }

        private static short CastStickValue(float stick_value) {
            return (short)Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, stick_value * (stick_value > 0 ? Int16.MaxValue : -Int16.MinValue)));
        }

        private static byte CastStickValueByte(float stick_value) {
            return (byte)Math.Max(Byte.MinValue, Math.Min(Byte.MaxValue, 127 - stick_value * Byte.MaxValue));
        }

        public void SetRumble(float low_freq, float high_freq, float amp) {
            if (state <= Joycon.state_.ATTACHED) return;
            rumble_obj.set_vals(low_freq, high_freq, amp);
        }

        // Run from poll thread
        private void SendRumble(byte[] buf, byte[] data) {
            Array.Clear(buf);

            buf[0] = 0x10;
            buf[1] = global_count;
            if (global_count == 0xf) global_count = 0;
            else ++global_count;
            Array.Copy(data, 0, buf, 2, 8);
            PrintArray(buf, DebugType.RUMBLE, len: 10, format: "Rumble data sent: {0:S}");
            HIDapi.hid_write(handle, buf, new UIntPtr(report_len));
        }

        private byte[] Subcommand(byte sc, byte[] buf, uint len, bool print = true) {
            var buf_ = new byte[report_len];
            Array.Clear(buf_);

            Array.Copy(default_buf, 0, buf_, 2, 8);
            Array.Copy(buf, 0, buf_, 11, len);
            buf_[10] = sc;
            buf_[1] = global_count;
            buf_[0] = 0x1;
            if (global_count == 0xf) global_count = 0;
            else ++global_count;
            if (print) {
                PrintArray(buf_, DebugType.COMMS, len, 11, "Subcommand 0x" + string.Format("{0:X2}", sc) + " sent. Data: 0x{0:S}");
            }
            HIDapi.hid_write(handle, buf_, new UIntPtr(len + 11));
            
            ref var response = ref buf_;
            int tries = 0;
            int length = 0;
            bool responseFound = false;
            do {
                length = HIDapi.hid_read_timeout(handle, response, new UIntPtr(report_len), 100); // don't set the timeout lower than 100 or might not always work
                responseFound = (length >= 20 && response[0] == 0x21 && response[14] == sc);
                tries++;
            } while (tries < 10 && !responseFound);

            if (!responseFound) {
                DebugPrint("No response.", DebugType.COMMS);
                return null;
            }
            if (print) {
                PrintArray(response, DebugType.COMMS, (uint)length - 1, 1, "Response ID 0x" + string.Format("{0:X2}", response[0]) + ". Data: 0x{0:S}");
            } 
            return response;
        }

        private bool dump_calibration_data() {
            if (isSnes || isThirdParty) {
                return true;
            }
            bool ok = true;
            byte[] buf_ = ReadSPICheck(0x80, (isLeft ? (byte)0x12 : (byte)0x1d), 9, ref ok); // get user calibration data if possible
            bool found = false;
            if (ok)
            {
                for (int i = 0; i < 9; ++i) {
                    if (buf_[i] != 0xff) {
                        form.AppendTextBox("Using user stick calibration data.\r\n");
                        found = true;
                        break;
                    }
                }
                if (!found) {
                    form.AppendTextBox("Using factory stick calibration data.\r\n");
                    buf_ = ReadSPICheck(0x60, (isLeft ? (byte)0x3d : (byte)0x46), 9, ref ok);
                }
            }

            stick_cal[isLeft ? 0 : 2] = (UInt16)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
            stick_cal[isLeft ? 1 : 3] = (UInt16)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
            stick_cal[isLeft ? 2 : 4] = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
            stick_cal[isLeft ? 3 : 5] = (UInt16)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
            stick_cal[isLeft ? 4 : 0] = (UInt16)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
            stick_cal[isLeft ? 5 : 1] = (UInt16)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

            PrintArray(stick_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

            if (isPro) {
                buf_ = ReadSPICheck(0x80, (!isLeft ? (byte)0x12 : (byte)0x1d), 9, ref ok); // get user calibration data if possible
                found = false;
                if (ok)
                {
                    for (int i = 0; i < 9; ++i) {
                        if (buf_[i] != 0xff) {
                            form.AppendTextBox("Using user stick calibration data.\r\n");
                            found = true;
                            break;
                        }
                    }
                    if (!found) {
                        form.AppendTextBox("Using factory stick calibration data.\r\n");
                        buf_ = ReadSPICheck(0x60, (!isLeft ? (byte)0x3d : (byte)0x46), 9, ref ok);
                    }
                }

                stick2_cal[!isLeft ? 0 : 2] = (UInt16)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
                stick2_cal[!isLeft ? 1 : 3] = (UInt16)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
                stick2_cal[!isLeft ? 2 : 4] = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
                stick2_cal[!isLeft ? 3 : 5] = (UInt16)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
                stick2_cal[!isLeft ? 4 : 0] = (UInt16)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
                stick2_cal[!isLeft ? 5 : 1] = (UInt16)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

                PrintArray(stick2_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

                buf_ = ReadSPICheck(0x60, (!isLeft ? (byte)0x86 : (byte)0x98), 16, ref ok);
                deadzone2 = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]);
            }

            buf_ = ReadSPICheck(0x60, (isLeft ? (byte)0x86 : (byte)0x98), 16, ref ok);
            deadzone = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]);

            buf_ = ReadSPICheck(0x80, 0x28, 10, ref ok);
            acc_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            acc_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            acc_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPICheck(0x80, 0x2E, 10, ref ok);
            acc_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            acc_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            acc_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPICheck(0x80, 0x34, 10, ref ok);
            gyr_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            gyr_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            gyr_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPICheck(0x80, 0x3A, 10, ref ok);
            gyr_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            gyr_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            gyr_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            PrintArray(gyr_neutral, len: 3, d: DebugType.IMU, format: "User gyro neutral position: {0:S}");

            // This is an extremely messy way of checking to see whether there is user stick calibration data present, but I've seen conflicting user calibration data on blank Joy-Cons. Worth another look eventually.
            if (gyr_neutral[0] + gyr_neutral[1] + gyr_neutral[2] == -3 || Math.Abs(gyr_neutral[0]) > 100 || Math.Abs(gyr_neutral[1]) > 100 || Math.Abs(gyr_neutral[2]) > 100) {
                buf_ = ReadSPICheck(0x60, 0x20, 10, ref ok);
                acc_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                acc_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                acc_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPICheck(0x60, 0x26, 10, ref ok);
                acc_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                acc_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                acc_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPICheck(0x60, 0x2C, 10, ref ok);
                gyr_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                gyr_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                gyr_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPICheck(0x60, 0x32, 10, ref ok);
                gyr_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                gyr_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                gyr_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                PrintArray(gyr_neutral, len: 3, d: DebugType.IMU, format: "Factory gyro neutral position: {0:S}");
            }

            if (!ok) {
                form.AppendTextBox("Error while reading calibration data.\r\n");
            }
            return ok;
        }

        private int ReadUSBCheck(byte[] data, byte command) {
            int length;
            bool responseFound;
            int tries = 0;
            do {
                length = HIDapi.hid_read_timeout(handle, data, new UIntPtr(report_len), 100);
                responseFound = (length > 1 && data[0] == 0x81 && data[1] == command);
                ++tries;
            } while (tries < 10 && !responseFound);

            if (!responseFound) {
                length = 0;
            }
            
            return length;
        }

        private byte[] ReadSPICheck(byte addr1, byte addr2, uint len, ref bool ok, bool print = false) {
            byte[] read_buf = new byte[len];
            if (!ok) {
                return read_buf;
            }

            byte[] buf = { addr2, addr1, 0x00, 0x00, (byte)len };
            byte[] buf_ = null;

            ok = false;
            for (int i = 0; i < 5; ++i) {
                buf_ = Subcommand(0x10, buf, 5, false);
                if (buf_ != null && buf_[15] == addr2 && buf_[16] == addr1) {
                    ok = true;
                    break;
                }
            }
            if (ok) {
                Array.Copy(buf_, 20, read_buf, 0, len);
                if (print) PrintArray(read_buf, DebugType.COMMS, len);
            }
            else {
                form.AppendTextBox("ReadSPI error\r\n");
            }
            return read_buf;
        }

        private void PrintArray<T>(T[] arr, DebugType d = DebugType.NONE, uint len = 0, uint start = 0, string format = "{0:S}") {
            if (d != debug_type && debug_type != DebugType.ALL) return;
            if (len == 0) len = (uint)arr.Length;
            string tostr = "";
            for (int i = 0; i < len; ++i) {
                tostr += string.Format((arr[0] is byte) ? "{0:X2} " : ((arr[0] is float) ? "{0:F} " : "{0:D} "), arr[i + start]);
            }
            DebugPrint(string.Format(format, tostr), d);
        }

        private static OutputControllerXbox360InputState MapToXbox360Input(Joycon input) {
            var output = new OutputControllerXbox360InputState();

            var swapAB = input.swapAB;
            var swapXY = input.swapXY;

            var isPro = input.isPro;
            var isLeft = input.isLeft;
            var isSnes = input.isSnes;
            var other = input.other;
            var GyroAnalogSliders = input.GyroAnalogSliders;

            var buttons = input.buttons;
            var stick = input.stick;
            var stick2 = input.stick2;
            var sliderVal = input.sliderVal;

            if (isPro) {
                output.a = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.b = buttons[(int)(!swapAB ? Button.A : Button.B)];
                output.y = buttons[(int)(!swapXY ? Button.X : Button.Y)];
                output.x = buttons[(int)(!swapXY ? Button.Y : Button.X)];

                output.dpad_up = buttons[(int)Button.DPAD_UP];
                output.dpad_down = buttons[(int)Button.DPAD_DOWN];
                output.dpad_left = buttons[(int)Button.DPAD_LEFT];
                output.dpad_right = buttons[(int)Button.DPAD_RIGHT];

                output.back = buttons[(int)Button.MINUS];
                output.start = buttons[(int)Button.PLUS];
                output.guide = buttons[(int)Button.HOME];

                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];

                output.thumb_stick_left = buttons[(int)Button.STICK];
                output.thumb_stick_right = buttons[(int)Button.STICK2];
            } else {
                if (other != null) { // no need for && other != this
                    output.a = buttons[(int)(!swapAB ? isLeft ? Button.B : Button.DPAD_DOWN : isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.b = buttons[(int)(swapAB ? isLeft ? Button.B : Button.DPAD_DOWN : isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.y = buttons[(int)(!swapXY ? isLeft ? Button.X : Button.DPAD_UP : isLeft ? Button.Y : Button.DPAD_LEFT)];
                    output.x = buttons[(int)(swapXY ? isLeft ? Button.X : Button.DPAD_UP : isLeft ? Button.Y : Button.DPAD_LEFT)];

                    output.dpad_up = buttons[(int)(isLeft ? Button.DPAD_UP : Button.X)];
                    output.dpad_down = buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.B)];
                    output.dpad_left = buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)];
                    output.dpad_right = buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)];

                    output.back = buttons[(int)Button.MINUS];
                    output.start = buttons[(int)Button.PLUS];
                    output.guide = buttons[(int)Button.HOME];

                    output.shoulder_left = buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER2_1)];
                    output.shoulder_right = buttons[(int)(isLeft ? Button.SHOULDER2_1 : Button.SHOULDER_1)];

                    output.thumb_stick_left = buttons[(int)(isLeft ? Button.STICK : Button.STICK2)];
                    output.thumb_stick_right = buttons[(int)(isLeft ? Button.STICK2 : Button.STICK)];
                } else { // single joycon mode
                    output.a = buttons[(int)(!swapAB ? isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT : isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.b = buttons[(int)(swapAB ? isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT : isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.y = buttons[(int)(!swapXY ? isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT : isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];
                    output.x = buttons[(int)(swapXY ? isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT : isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];

                    output.back = buttons[(int)Button.MINUS] | buttons[(int)Button.HOME];
                    output.start = buttons[(int)Button.PLUS] | buttons[(int)Button.CAPTURE];

                    output.shoulder_left = buttons[(int)Button.SL];
                    output.shoulder_right = buttons[(int)Button.SR];

                    output.thumb_stick_left = buttons[(int)Button.STICK];
                }
            }

            // overwrite guide button if it's custom-mapped
            if (Config.Value("home") != "0")
                output.guide = false;

            if (!isSnes) {
                if (other != null || isPro) { // no need for && other != this
                    output.axis_left_x = CastStickValue((other == input && !isLeft) ? stick2[0] : stick[0]);
                    output.axis_left_y = CastStickValue((other == input && !isLeft) ? stick2[1] : stick[1]);

                    output.axis_right_x = CastStickValue((other == input && !isLeft) ? stick[0] : stick2[0]);
                    output.axis_right_y = CastStickValue((other == input && !isLeft) ? stick[1] : stick2[1]);
                } else { // single joycon mode
                    output.axis_left_y = CastStickValue((isLeft ? 1 : -1) * stick[0]);
                    output.axis_left_x = CastStickValue((isLeft ? -1 : 1) * stick[1]);
                }
            }

            if (isPro || other != null) {
                byte lval = GyroAnalogSliders ? sliderVal[0] : Byte.MaxValue;
                byte rval = GyroAnalogSliders ? sliderVal[1] : Byte.MaxValue;
                output.trigger_left = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2)] ? lval : 0);
                output.trigger_right = (byte)(buttons[(int)(isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2)] ? rval : 0);
            } else {
                output.trigger_left = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER_1)] ? Byte.MaxValue : 0);
                output.trigger_right = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER_2)] ? Byte.MaxValue : 0);
            }

            return output;
        }

        public static OutputControllerDualShock4InputState MapToDualShock4Input(Joycon input) {
            var output = new OutputControllerDualShock4InputState();

            var swapAB = input.swapAB;
            var swapXY = input.swapXY;

            var isPro = input.isPro;
            var isLeft = input.isLeft;
            var isSnes = input.isSnes;
            var other = input.other;
            var GyroAnalogSliders = input.GyroAnalogSliders;

            var buttons = input.buttons;
            var stick = input.stick;
            var stick2 = input.stick2;
            var sliderVal = input.sliderVal;

            if (isPro) {
                output.cross = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.circle = buttons[(int)(!swapAB ? Button.A : Button.B)];
                output.triangle = buttons[(int)(!swapXY ? Button.X : Button.Y)];
                output.square = buttons[(int)(!swapXY ? Button.Y : Button.X)];


                if (buttons[(int)Button.DPAD_UP]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Northwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Northeast;
                    else
                        output.dPad = DpadDirection.North;
                } else if (buttons[(int)Button.DPAD_DOWN]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Southwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Southeast;
                    else
                        output.dPad = DpadDirection.South;
                } else if (buttons[(int)Button.DPAD_LEFT])
                    output.dPad = DpadDirection.West;
                else if (buttons[(int)Button.DPAD_RIGHT])
                    output.dPad = DpadDirection.East;

                output.share = buttons[(int)Button.CAPTURE];
                output.options = buttons[(int)Button.PLUS];
                output.ps = buttons[(int)Button.HOME];
                output.touchpad = buttons[(int)Button.MINUS];
                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];
                output.thumb_left = buttons[(int)Button.STICK];
                output.thumb_right = buttons[(int)Button.STICK2];
            } else {
                if (other != null) { // no need for && other != this
                    output.cross = !swapAB ? buttons[(int)(isLeft ? Button.B : Button.DPAD_DOWN)] : buttons[(int)(isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.circle = swapAB ? buttons[(int)(isLeft ? Button.B : Button.DPAD_DOWN)] : buttons[(int)(isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.triangle = !swapXY ? buttons[(int)(isLeft ? Button.X : Button.DPAD_UP)] : buttons[(int)(isLeft ? Button.Y : Button.DPAD_LEFT)];
                    output.square = swapXY ? buttons[(int)(isLeft ? Button.X : Button.DPAD_UP)] : buttons[(int)(isLeft ? Button.Y : Button.DPAD_LEFT)];

                    if (buttons[(int)(isLeft ? Button.DPAD_UP : Button.X)])
                        if (buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)])
                            output.dPad = DpadDirection.Northwest;
                        else if (buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)])
                            output.dPad = DpadDirection.Northeast;
                        else
                            output.dPad = DpadDirection.North;
                    else if (buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.B)])
                        if (buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)])
                            output.dPad = DpadDirection.Southwest;
                        else if (buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)])
                            output.dPad = DpadDirection.Southeast;
                        else
                            output.dPad = DpadDirection.South;
                    else if (buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)])
                        output.dPad = DpadDirection.West;
                    else if (buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)])
                        output.dPad = DpadDirection.East;

                    output.share = buttons[(int)Button.CAPTURE];
                    output.options = buttons[(int)Button.PLUS];
                    output.ps = buttons[(int)Button.HOME];
                    output.touchpad = buttons[(int)Button.MINUS];
                    output.shoulder_left = buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER2_1)];
                    output.shoulder_right = buttons[(int)(isLeft ? Button.SHOULDER2_1 : Button.SHOULDER_1)];
                    output.thumb_left = buttons[(int)(isLeft ? Button.STICK : Button.STICK2)];
                    output.thumb_right = buttons[(int)(isLeft ? Button.STICK2 : Button.STICK)];
                } else { // single joycon mode
                    output.cross = !swapAB ? buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT)] : buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.circle = swapAB ? buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT)] : buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.triangle = !swapXY ? buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT)] : buttons[(int)(isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];
                    output.square = swapXY ? buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT)] : buttons[(int)(isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];

                    output.ps = buttons[(int)Button.MINUS] | buttons[(int)Button.HOME];
                    output.options = buttons[(int)Button.PLUS] | buttons[(int)Button.CAPTURE];

                    output.shoulder_left = buttons[(int)Button.SL];
                    output.shoulder_right = buttons[(int)Button.SR];

                    output.thumb_left = buttons[(int)Button.STICK];
                }
            }

            // overwrite guide button if it's custom-mapped
            if (Config.Value("home") != "0")
                output.ps = false;

            if (!isSnes) {
                if (other != null || isPro) { // no need for && other != this
                    output.thumb_left_x = CastStickValueByte((other == input && !isLeft) ? -stick2[0] : -stick[0]);
                    output.thumb_left_y = CastStickValueByte((other == input && !isLeft) ? stick2[1] : stick[1]);
                    output.thumb_right_x = CastStickValueByte((other == input && !isLeft) ? -stick[0] : -stick2[0]);
                    output.thumb_right_y = CastStickValueByte((other == input && !isLeft) ? stick[1] : stick2[1]);
                } else { // single joycon mode
                    output.thumb_left_y = CastStickValueByte((isLeft ? 1 : -1) * stick[0]);
                    output.thumb_left_x = CastStickValueByte((isLeft ? 1 : -1) * stick[1]);
                }
            }

            if (isPro || other != null) {
                byte lval = GyroAnalogSliders ? sliderVal[0] : Byte.MaxValue;
                byte rval = GyroAnalogSliders ? sliderVal[1] : Byte.MaxValue;
                output.trigger_left_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2)] ? lval : 0);
                output.trigger_right_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2)] ? rval : 0);
            } else {
                output.trigger_left_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER_1)] ? Byte.MaxValue : 0);
                output.trigger_right_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER_2)] ? Byte.MaxValue : 0);
            }
            // Output digital L2 / R2 in addition to analog L2 / R2
            output.trigger_left = output.trigger_left_value > 0;
            output.trigger_right = output.trigger_right_value > 0;

            return output;
        }

        public string getControllerName() {
            return isPro ? "Pro Controller" : (isSnes ? "SNES Controller" : (isLeft ? "Joycon Left" : "Joycon Right"));
        }
    }
}
