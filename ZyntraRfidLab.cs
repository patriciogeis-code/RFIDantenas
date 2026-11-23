using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("Zyntra RFID Lab")]
[assembly: AssemblyDescription("Consola de Control y Calibración de Antenas y Arcos RFID UHF (BRD-DC06 / BRD-DC01 / BRD-2204)")]
[assembly: AssemblyCompany("Zyntra")]
[assembly: AssemblyProduct("Zyntra RFID Lab")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace ZyntraRfidLab
{
    public static class AutoUpdater
    {
        public const string CurrentVersion = "1.2.2";
        public static string GithubRepo = "patriciogeis-code/RFIDantenas";
        public static string LastCheckStatus = "v" + CurrentVersion + " (Actualizado)";
        public static string RemoteVersion = CurrentVersion;
        public static bool UpdateAvailable = false;

        public static void CleanupOldBinary()
        {
            try
            {
                string oldFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Zyntra RFID Lab.exe.old");
                if (File.Exists(oldFile)) File.Delete(oldFile);
            }
            catch { }
        }

        public static bool CheckForUpdate(out string message)
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                string rawUrl = "https://raw.githubusercontent.com/" + GithubRepo + "/main/version.json?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "ZyntraRfidLab-Updater");
                    wc.Encoding = Encoding.UTF8;
                    string json = wc.DownloadString(rawUrl);
                    string ver = ExtractVer(json);
                    if (!string.IsNullOrEmpty(ver))
                    {
                        RemoteVersion = ver;
                        if (IsNewerVersion(ver, CurrentVersion))
                        {
                            UpdateAvailable = true;
                            LastCheckStatus = "Nueva versión disponible: v" + ver;
                            message = LastCheckStatus;
                            return true;
                        }
                    }
                }
                UpdateAvailable = false;
                LastCheckStatus = "Tienes la última versión (v" + CurrentVersion + ")";
                message = LastCheckStatus;
                return false;
            }
            catch
            {
                LastCheckStatus = "v" + CurrentVersion + " (Listo para auto-actualizar desde GitHub)";
                message = LastCheckStatus;
                return false;
            }
        }

        public static bool ApplySelfUpdate(out string message)
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string rawBase = "https://raw.githubusercontent.com/" + GithubRepo + "/main/";

                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "ZyntraRfidLab-Updater");

                    // 1. Update Web UI (ui/index.html)
                    string uiPath = Path.Combine(baseDir, "ui", "index.html");
                    string newHtml = wc.DownloadString(rawBase + "ui/index.html?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    if (!string.IsNullOrEmpty(newHtml) && newHtml.Contains("Zyntra RFID Lab"))
                    {
                        File.WriteAllText(uiPath, newHtml, Encoding.UTF8);
                    }

                    // 2. Download new EXE to temp file and hot-swap running binary
                    string exePath = Path.Combine(baseDir, "Zyntra RFID Lab.exe");
                    string tmpExe = Path.Combine(baseDir, "Zyntra RFID Lab.exe.new");
                    string oldExe = Path.Combine(baseDir, "Zyntra RFID Lab.exe.old");

                    wc.DownloadFile(rawBase + "Zyntra%20RFID%20Lab.exe?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), tmpExe);
                    if (File.Exists(tmpExe) && new FileInfo(tmpExe).Length > 20000)
                    {
                        if (File.Exists(oldExe)) File.Delete(oldExe);
                        File.Move(exePath, oldExe);
                        File.Move(tmpExe, exePath);

                        // Restart new EXE automatically
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            WorkingDirectory = baseDir
                        });
                        Environment.Exit(0);
                    }
                }
                message = "Actualizado correctamente a v" + RemoteVersion;
                return true;
            }
            catch (Exception ex)
            {
                message = "Error al descargar actualización desde GitHub: " + ex.Message;
                return false;
            }
        }

        private static bool IsNewerVersion(string remote, string local)
        {
            try
            {
                Version vRem = new Version(remote.TrimStart('v', 'V'));
                Version vLoc = new Version(local.TrimStart('v', 'V'));
                return vRem > vLoc;
            }
            catch
            {
                return remote != local;
            }
        }

        private static string ExtractVer(string json)
        {
            int idx = json.IndexOf("\"version\"", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int col = json.IndexOf(":", idx);
            int q1 = json.IndexOf("\"", col + 1);
            int q2 = json.IndexOf("\"", q1 + 1);
            if (q1 > 0 && q2 > q1) return json.Substring(q1 + 1, q2 - q1 - 1).Trim();
            return null;
        }
    }

    public class TagReadRecord
    {
        public string EpcHex;
        public string EpcAscii;
        public string TidHex;
        public string ChipModel;
        public int Antenna;
        public int RssiRaw;
        public int RssiDbm;
        public double FreqMhz;
        public int ReadCount;
        public long FirstSeenMs;
        public long LastSeenMs;
        public string SourceDevice; // "DC06", "DC01", "GATE"
    }

    public static class GxProtocol
    {
        public static ushort Crc16(byte[] data, int offset, int length)
        {
            ushort crc = 0x0000;
            for (int i = offset; i < offset + length; i++)
            {
                crc ^= (ushort)(data[i] << 8);
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x8000) != 0)
                        crc = (ushort)((crc << 1) ^ 0x1021);
                    else
                        crc <<= 1;
                }
            }
            return crc;
        }

        public static byte[] BuildFrame(byte category, byte mid, byte[] payload)
        {
            int payLen = payload != null ? payload.Length : 0;
            byte[] frame = new byte[1 + 4 + 2 + payLen + 2];
            frame[0] = 0x5A;
            frame[1] = 0x00;
            frame[2] = 0x01;
            frame[3] = category;
            frame[4] = mid;
            frame[5] = (byte)((payLen >> 8) & 0xFF);
            frame[6] = (byte)(payLen & 0xFF);
            if (payLen > 0)
                Array.Copy(payload, 0, frame, 7, payLen);
            ushort crc = Crc16(frame, 1, 6 + payLen);
            frame[7 + payLen] = (byte)((crc >> 8) & 0xFF);
            frame[8 + payLen] = (byte)(crc & 0xFF);
            return frame;
        }

        public static string DecodeChipFromTid(string tidHex)
        {
            if (string.IsNullOrEmpty(tidHex) || tidHex.Length < 8)
                return "EPC Gen2 UHF";
            string prefix = tidHex.Substring(0, 8).ToUpperInvariant();
            if (prefix.StartsWith("E28011B0")) return "Impinj M730 / Monza R6-P";
            if (prefix.StartsWith("E2801190") || prefix.StartsWith("E2801191")) return "Impinj M750 / M730";
            if (prefix.StartsWith("E28011A0")) return "Impinj M770";
            if (prefix.StartsWith("E28011C0") || prefix.StartsWith("E28011C1")) return "Impinj M830 / M850";
            if (prefix.StartsWith("E2801170")) return "Impinj Monza R6";
            if (prefix.StartsWith("E28011")) return "Impinj Original IC";
            if (prefix.StartsWith("E2806894") || prefix.StartsWith("E2806994")) return "NXP UCODE 8";
            if (prefix.StartsWith("E2806995")) return "NXP UCODE 9";
            if (prefix.StartsWith("E28068") || prefix.StartsWith("E28069")) return "NXP UCODE Series";
            if (prefix.StartsWith("E20034") || prefix.StartsWith("E28034")) return "Alien Higgs";
            return "ISO 18000-6C (" + prefix.Substring(0, 6) + ")";
        }

        public static string HexToCleanAscii(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length < 2) return "";
            int byteCount = hex.Length / 2;
            byte[] bytes = new byte[byteCount];
            for (int i = 0; i < byteCount; i++)
            {
                int b;
                if (!int.TryParse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out b))
                    return "";
                bytes[i] = (byte)b;
            }

            // Trim trailing 0x00 / 0xFF padding
            int end = byteCount;
            while (end > 0 && (bytes[end - 1] == 0x00 || bytes[end - 1] == 0xFF))
                end--;

            if (end == 0) return "";

            // 1. Check if ALL payload bytes are valid printable ASCII (0x20..0x7E)
            bool allAscii = true;
            for (int i = 0; i < end; i++)
            {
                if (bytes[i] < 0x20 || bytes[i] > 0x7E)
                {
                    allAscii = false;
                    break;
                }
            }
            if (allAscii)
            {
                return Encoding.ASCII.GetString(bytes, 0, end).Trim();
            }

            // 2. Check GS1 SGTIN-96 (12 bytes = 96 bits, Header = 0x30)
            if (byteCount == 12 && bytes[0] == 0x30)
            {
                string sgtin = TryDecodeSgtin96(bytes);
                if (!string.IsNullOrEmpty(sgtin))
                    return sgtin;
            }

            // 3. Otherwise it is raw binary/factory HEX (do NOT invent fake characters like "G7" or "0:")
            return "";
        }

        public static string TryDecodeSgtin96(byte[] bytes)
        {
            try
            {
                if (bytes == null || bytes.Length != 12 || bytes[0] != 0x30) return "";
                int partition = (bytes[1] >> 2) & 0x07;
                if (partition > 6) return "";

                int[] compBitsTbl = { 40, 37, 34, 30, 27, 24, 20 };
                int[] compDigTbl  = { 12, 11, 10, 9,  8,  7,  6  };
                int[] itemBitsTbl = { 4,  7,  10, 14, 17, 20, 24 };
                int[] itemDigTbl  = { 1,  2,  3,  4,  5,  6,  7  };

                int compBits = compBitsTbl[partition];
                int compDig  = compDigTbl[partition];
                int itemBits = itemBitsTbl[partition];
                int itemDig  = itemDigTbl[partition];

                ulong company = ReadBits(bytes, 14, compBits);
                ulong itemRef = ReadBits(bytes, 14 + compBits, itemBits);
                ulong serial  = ReadBits(bytes, 14 + compBits + itemBits, 38);

                string compStr = company.ToString().PadLeft(compDig, '0');
                string itemStr = itemRef.ToString().PadLeft(itemDig, '0');
                if (compStr.Length > compDig || itemStr.Length > itemDig) return "";

                string indicator = itemStr.Substring(0, 1);
                string itemBody = itemStr.Substring(1);
                string gtin13NoCheck = indicator + compStr + itemBody;
                if (gtin13NoCheck.Length != 13) return "";

                int sum = 0;
                for (int i = 0; i < 13; i++)
                {
                    int d = gtin13NoCheck[i] - '0';
                    sum += (i % 2 == 0) ? d * 3 : d;
                }
                int check = (10 - (sum % 10)) % 10;
                string gtin = (indicator == "0") ? (gtin13NoCheck.Substring(1) + check) : (gtin13NoCheck + check);
                return "GS1 " + gtin + " #" + serial;
            }
            catch { return ""; }
        }

        private static ulong ReadBits(byte[] data, int bitOffset, int bitCount)
        {
            ulong val = 0;
            for (int i = 0; i < bitCount; i++)
            {
                int pos = bitOffset + i;
                int b = (data[pos >> 3] >> (7 - (pos & 7))) & 1;
                val = (val << 1) | (ulong)b;
            }
            return val;
        }

        public static string AsciiToHexPadded(string text, int byteLen)
        {
            byte[] raw = Encoding.ASCII.GetBytes(text ?? "");
            byte[] buf = new byte[byteLen];
            Array.Copy(raw, 0, buf, 0, Math.Min(raw.Length, byteLen));
            StringBuilder sb = new StringBuilder(byteLen * 2);
            foreach (byte b in buf) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }
    }

    public class UsbGxReaderManager
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile
        );

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_FlushQueue(SafeFileHandle HidDeviceObject);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1;
        private const uint FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        public string DeviceRole = "DC06";
        public string DevicePath = null;
        public bool Connected = false;
        public bool Scanning = false;
        public string SerialNumber = "BRDC600002328A000282";
        public string CompileDate = "Apr 12 2023";
        public int PowerDbm = 27;
        public int MaxPowerDbm = 27;
        public bool BeepEnabled = true;
        public bool KbdWedgeEnabled = false;
        public long TotalReads = 0;
        public int ReadsPerSec = 0;
        public int RxPacketsCount = 0;
        public string LastRawRx = "";
        public string LastStatusMessage = "Conectado y listo";

        private SafeFileHandle _hRead;
        private SafeFileHandle _hWrite;
        private FileStream _fsRead;
        private FileStream _fsWrite;
        private CancellationTokenSource _cts;
        private readonly object _writeLock = new object();
        private int _readsInCurrentSec = 0;
        private long _lastRpsTickMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public long LastRxOrPollMs = 0;

        public ConcurrentDictionary<string, TagReadRecord> Tags = new ConcurrentDictionary<string, TagReadRecord>();
        public TagReadRecord LatestTag = null;

        public static List<string> FindAllGxHidPaths()
        {
            List<string> list = new List<string>();
            const string guidKey = @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{4d1e55b2-f16f-11cf-88cb-001111000030}";
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(guidKey))
                {
                    if (key != null)
                    {
                        foreach (string subName in key.GetSubKeyNames())
                        {
                            if (subName.IndexOf("vid_03eb&pid_2421&mi_00", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                string devPath = @"\\?\" + subName.Substring(4);
                                SafeFileHandle h = CreateFile(devPath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                                if (!h.IsInvalid)
                                {
                                    h.Close();
                                    list.Add(devPath);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        public bool Connect(string path)
        {
            Disconnect();
            try
            {
                _hRead = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                _hWrite = CreateFile(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (_hRead.IsInvalid || _hWrite.IsInvalid)
                {
                    Connected = false;
                    return false;
                }
                _fsRead = new FileStream(_hRead, FileAccess.Read, 65, true);
                _fsWrite = new FileStream(_hWrite, FileAccess.Write, 65, true);
                DevicePath = path;
                Connected = true;
                _cts = new CancellationTokenSource();
                Task.Run(() => ReadLoop(_cts.Token));
                Thread.Sleep(80);

                // Initialize in Command Mode: Stop -> GetReaderInfo -> SetPower -> SetBaseband (Session 0, Q=1, Dual Target A/B, AutoDormancy OFF)
                SendFrame(GxProtocol.BuildFrame(0x02, 0xFF, null));
                Thread.Sleep(150);
                SendFrame(GxProtocol.BuildFrame(0x01, 0x00, null)); // GetReaderInfo
                Thread.Sleep(120);
                SendFrame(GxProtocol.BuildFrame(0x02, 0x0D, new byte[] { 0x00 })); // AutoDormancy OFF
                Thread.Sleep(120);
                SendFrame(GxProtocol.BuildFrame(0x02, 0x0B, new byte[] { 0x01, 0x01, 0x02, 0x01, 0x03, 0x00, 0x04, 0x02 })); // Miller-4, Q=1, Session 0, Dual Target A/B
                Thread.Sleep(150);
                SendFrame(GxProtocol.BuildFrame(0x02, 0x01, new byte[] { 0x01, (byte)PowerDbm })); // SetPower
                Thread.Sleep(150);
                LastStatusMessage = "Conectado (S0 Instantáneo + Dual A/B Activo)";
                return true;
            }
            catch (Exception ex)
            {
                LastStatusMessage = "Error USB: " + ex.Message;
                Connected = false;
                return false;
            }
        }

        public void HandlePhysicalDisconnect()
        {
            Connected = false;
            Scanning = false;
            ReadsPerSec = 0;
            LastStatusMessage = "🔴 Antena desconectada del USB — Esperando conexión...";
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_fsRead != null) _fsRead.Dispose(); } catch { }
            try { if (_fsWrite != null) _fsWrite.Dispose(); } catch { }
            try { if (_hRead != null && !_hRead.IsInvalid) _hRead.Close(); } catch { }
            try { if (_hWrite != null && !_hWrite.IsInvalid) _hWrite.Close(); } catch { }
        }

        public static bool IsDevicePathPresent(string devPath)
        {
            if (string.IsNullOrEmpty(devPath)) return false;
            try
            {
                SafeFileHandle h = CreateFile(devPath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (!h.IsInvalid)
                {
                    h.Close();
                    return true;
                }
            }
            catch { }
            return false;
        }

        public void Disconnect()
        {
            try
            {
                if (Scanning) StopInventory();
            }
            catch { }
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_fsRead != null) _fsRead.Dispose(); } catch { }
            try { if (_fsWrite != null) _fsWrite.Dispose(); } catch { }
            try { if (_hRead != null && !_hRead.IsInvalid) _hRead.Close(); } catch { }
            try { if (_hWrite != null && !_hWrite.IsInvalid) _hWrite.Close(); } catch { }
            Connected = false;
            Scanning = false;
        }

        public bool SendFrame(byte[] frame)
        {
            if (!Connected || _fsWrite == null) return false;
            lock (_writeLock)
            {
                try
                {
                    byte[] outBuf = new byte[65];
                    outBuf[0] = 0x00;
                    Array.Copy(frame, 0, outBuf, 1, Math.Min(frame.Length, 64));
                    var ar = _fsWrite.BeginWrite(outBuf, 0, 65, null, null);
                    if (ar.AsyncWaitHandle.WaitOne(400))
                    {
                        _fsWrite.EndWrite(ar);
                        return true;
                    }
                    HandlePhysicalDisconnect();
                    return false;
                }
                catch
                {
                    HandlePhysicalDisconnect();
                    return false;
                }
            }
        }

        public void StartInventory()
        {
            if (!Connected)
            {
                var paths = FindAllGxHidPaths();
                if (paths.Count > 0) Connect(paths[0]);
            }
            if (!Connected) return;

            // Start continuous inventory on Antenna 1 (pure EPC mode for maximum distance and speed)
            LastRxOrPollMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Scanning = true;
            SendFrame(GxProtocol.BuildFrame(0x02, 0x10, new byte[] { 0x00, 0x00, 0x00, 0x01, 0x01 }));
            LastStatusMessage = "Escaneando en vivo (" + PowerDbm + " dBm)";
        }

        public void StopInventory()
        {
            if (!Connected) return;
            SendFrame(GxProtocol.BuildFrame(0x02, 0xFF, null));
            Scanning = false;
            ReadsPerSec = 0;
            LastStatusMessage = "En espera (" + PowerDbm + " dBm)";
        }

        public void SetPower(int dbm)
        {
            int clamped = Math.Max(0, Math.Min(MaxPowerDbm, dbm));
            bool wasScanning = Scanning;
            if (wasScanning)
            {
                SendFrame(GxProtocol.BuildFrame(0x02, 0xFF, null));
                Thread.Sleep(70);
            }
            SendFrame(GxProtocol.BuildFrame(0x02, 0x01, new byte[] { 0x01, (byte)clamped }));
            PowerDbm = clamped;
            Thread.Sleep(70);
            if (wasScanning)
            {
                SendFrame(GxProtocol.BuildFrame(0x02, 0x10, new byte[] { 0x00, 0x00, 0x00, 0x01, 0x01 }));
                Scanning = true;
                LastStatusMessage = "Potencia ajustada a " + PowerDbm + " dBm (Escaneando)";
            }
            else
            {
                LastStatusMessage = "Potencia ajustada a " + PowerDbm + " dBm";
            }
        }

        public void TriggerBeep()
        {
            bool wasScanning = Scanning;
            if (wasScanning)
            {
                SendFrame(GxProtocol.BuildFrame(0x02, 0xFF, null));
                Thread.Sleep(50);
            }
            // Single short beep: beepStatus=1, beepMode=1
            SendFrame(GxProtocol.BuildFrame(0x01, 0x1F, new byte[] { 0x01, 0x01 }));
            Thread.Sleep(80);
            if (wasScanning)
            {
                SendFrame(GxProtocol.BuildFrame(0x02, 0x10, new byte[] { 0x00, 0x00, 0x00, 0x01, 0x01 }));
            }
        }

        public bool WriteEpcTag(string newValue, bool isAscii, out string resultMsg)
        {
            if (!Connected)
            {
                resultMsg = "El lector USB no está conectado.";
                return false;
            }
            bool wasScanning = Scanning;
            if (wasScanning)
            {
                SendFrame(GxProtocol.BuildFrame(0x02, 0xFF, null));
                Thread.Sleep(90);
            }

            string hexToWrite;
            if (isAscii)
            {
                hexToWrite = GxProtocol.AsciiToHexPadded(newValue, 16); // 16 bytes = 128-bit EPC
            }
            else
            {
                string clean = (newValue ?? "").Replace(" ", "").Replace("-", "").ToUpperInvariant();
                while (clean.Length < 24) clean += "0";
                if (clean.Length % 4 != 0) clean = clean.PadRight(clean.Length + (4 - clean.Length % 4), '0');
                hexToWrite = clean;
            }

            byte[] epcBytes = new byte[hexToWrite.Length / 2];
            for (int i = 0; i < epcBytes.Length; i++)
                epcBytes[i] = Convert.ToByte(hexToWrite.Substring(i * 2, 2), 16);

            // MsgBaseWriteEpc (0x02, 0x11):
            // Antenna=0x00000001 (4B), Area=0x01 EPC (1B), WordStart=0x0002 (2B), ByteLen (2B), Data
            int wordLen = epcBytes.Length / 2;
            ushort pcWord = (ushort)(wordLen << 11);
            byte[] fullPayload = new byte[4 + 1 + 2 + 2 + 2 + epcBytes.Length];
            fullPayload[0] = 0x00; fullPayload[1] = 0x00; fullPayload[2] = 0x00; fullPayload[3] = 0x01;
            fullPayload[4] = 0x01; // Area 1 = EPC bank
            fullPayload[5] = 0x00; fullPayload[6] = 0x01; // Start at Word 1 (PC + EPC)
            int totalBytes = 2 + epcBytes.Length;
            fullPayload[7] = (byte)((totalBytes >> 8) & 0xFF);
            fullPayload[8] = (byte)(totalBytes & 0xFF);
            fullPayload[9] = (byte)((pcWord >> 8) & 0xFF);
            fullPayload[10] = (byte)(pcWord & 0xFF);
            Array.Copy(epcBytes, 0, fullPayload, 11, epcBytes.Length);

            bool sent = SendFrame(GxProtocol.BuildFrame(0x02, 0x11, fullPayload));
            Thread.Sleep(180);
            if (BeepEnabled)
            {
                SendFrame(GxProtocol.BuildFrame(0x01, 0x1F, new byte[] { 0x01, 0x01 }));
                Thread.Sleep(70);
            }
            if (wasScanning)
            {
                SendFrame(GxProtocol.BuildFrame(0x02, 0x10, new byte[] { 0x00, 0x00, 0x00, 0x01, 0x01, 0x02, 0x00, 0x06 }));
                Scanning = true;
            }
            resultMsg = sent ? ("EPC grabado con éxito: " + hexToWrite) : "Error al enviar trama de escritura.";
            return sent;
        }

        private void ReadLoop(CancellationToken token)
        {
            byte[] buf = new byte[65];
            while (!token.IsCancellationRequested && Connected)
            {
                try
                {
                    var ar = _fsRead.BeginRead(buf, 0, 65, null, null);
                    while (!token.IsCancellationRequested && !ar.AsyncWaitHandle.WaitOne(250))
                    {
                    }
                    if (token.IsCancellationRequested) break;
                    int n = _fsRead.EndRead(ar);
                    if (n <= 0)
                    {
                        HandlePhysicalDisconnect();
                        break;
                    }
                    if (buf[1] == 0x5A)
                    {
                        RxPacketsCount++;
                        StringBuilder sbHex = new StringBuilder();
                        for (int i = 1; i <= 20; i++) sbHex.Append(buf[i].ToString("X2")).Append(" ");
                        LastRawRx = sbHex.ToString().Trim();
                        ProcessGxPacket(buf, 1);
                    }
                }
                catch
                {
                    if (!token.IsCancellationRequested)
                    {
                        HandlePhysicalDisconnect();
                    }
                    break;
                }
            }
        }

        private void ProcessGxPacket(byte[] buf, int offset)
        {
            byte cat = buf[offset + 3];
            byte mid = buf[offset + 4];
            int len = (buf[offset + 5] << 8) | buf[offset + 6];
            int payStart = offset + 7;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LastRxOrPollMs = nowMs;
            if (nowMs - _lastRpsTickMs >= 1000)
            {
                ReadsPerSec = _readsInCurrentSec;
                _readsInCurrentSec = 0;
                _lastRpsTickMs = nowMs;
            }

            if (cat == 0x12 && mid == 0x01)
            {
                // LogBaseEpcOver: BRD-DC06 finished a scan cycle; immediately re-trigger if Scanning is active!
                if (Scanning && Connected)
                {
                    Task.Run(() =>
                    {
                        Thread.Sleep(35);
                        if (Scanning && Connected)
                        {
                            SendFrame(GxProtocol.BuildFrame(0x02, 0x10, new byte[] { 0x00, 0x00, 0x00, 0x01, 0x01 }));
                        }
                    });
                }
                return;
            }

            if (cat == 0x01 && mid == 0x00 && len > 6)
            {
                // MsgAppGetReaderInfo
                int snLen = (buf[payStart] << 8) | buf[payStart + 1];
                if (snLen > 0 && snLen < 32 && payStart + 2 + snLen <= buf.Length)
                {
                    SerialNumber = Encoding.ASCII.GetString(buf, payStart + 2, snLen).Trim('\0');
                }
            }
            else if (cat == 0x02 && mid == 0x02 && len >= 2)
            {
                // MsgBaseGetPower
                PowerDbm = buf[payStart + 1];
            }
            else if (cat == 0x12 && mid == 0x00 && len >= 5)
            {
                // LogBaseEpcInfo (Tag Read)
                int epcLen = (buf[payStart] << 8) | buf[payStart + 1];
                if (epcLen <= 0 || payStart + 2 + epcLen + 3 > 65) return;

                StringBuilder sbEpc = new StringBuilder(epcLen * 2);
                for (int i = 0; i < epcLen; i++)
                    sbEpc.Append(buf[payStart + 2 + i].ToString("X2"));
                string epcHex = sbEpc.ToString();

                int pos = payStart + 2 + epcLen;
                ushort pc = (ushort)((buf[pos] << 8) | buf[pos + 1]);
                int antId = buf[pos + 2];
                pos += 3;

                int rssiRaw = 18;
                string tidHex = "";
                double freqMhz = 915.25;

                int endPos = Math.Min(64, payStart + len);
                while (pos < endPos)
                {
                    byte pid = buf[pos++];
                    if (pid == 0x01 && pos < endPos)
                    {
                        rssiRaw = buf[pos++];
                    }
                    else if (pid == 0x02 && pos < endPos)
                    {
                        pos++; // result byte
                    }
                    else if (pid == 0x03 && pos + 1 < endPos)
                    {
                        int tidLen = (buf[pos] << 8) | buf[pos + 1];
                        pos += 2;
                        StringBuilder sbTid = new StringBuilder(tidLen * 2);
                        for (int k = 0; k < tidLen && (pos + k) < endPos; k++)
                            sbTid.Append(buf[pos + k].ToString("X2"));
                        tidHex = sbTid.ToString();
                        pos += tidLen;
                    }
                    else if (pid == 0x06 && pos + 1 < endPos)
                    {
                        pos += 2;
                    }
                    else if (pid == 0x07 && pos < endPos)
                    {
                        pos += 1;
                    }
                    else if (pid == 0x08 && pos + 3 < endPos)
                    {
                        int freqKhz = (buf[pos] << 24) | (buf[pos + 1] << 16) | (buf[pos + 2] << 8) | buf[pos + 3];
                        if (freqKhz > 800000 && freqKhz < 1000000)
                            freqMhz = Math.Round(freqKhz / 1000.0, 2);
                        pos += 4;
                    }
                    else
                    {
                        break;
                    }
                }

                // Convert raw RSSI to dBm approximation (-85 to -30 dBm)
                int rssiDbm = -85 + (int)Math.Round((rssiRaw / 35.0) * 52.0);
                if (rssiDbm > -28) rssiDbm = -28;
                if (rssiDbm < -85) rssiDbm = -85;

                TotalReads++;
                _readsInCurrentSec++;

                string key = string.IsNullOrEmpty(tidHex) ? epcHex : (epcHex + "|" + tidHex);
                TagReadRecord rec = Tags.AddOrUpdate(key,
                    k => new TagReadRecord
                    {
                        EpcHex = epcHex,
                        EpcAscii = GxProtocol.HexToCleanAscii(epcHex),
                        TidHex = tidHex,
                        ChipModel = GxProtocol.DecodeChipFromTid(tidHex),
                        Antenna = antId,
                        RssiRaw = rssiRaw,
                        RssiDbm = rssiDbm,
                        FreqMhz = freqMhz,
                        ReadCount = 1,
                        FirstSeenMs = nowMs,
                        LastSeenMs = nowMs,
                        SourceDevice = DeviceRole
                    },
                    (k, existing) =>
                    {
                        existing.EpcHex = epcHex;
                        existing.EpcAscii = GxProtocol.HexToCleanAscii(epcHex);
                        if (!string.IsNullOrEmpty(tidHex))
                        {
                            existing.TidHex = tidHex;
                            existing.ChipModel = GxProtocol.DecodeChipFromTid(tidHex);
                        }
                        existing.Antenna = antId;
                        existing.RssiRaw = rssiRaw;
                        existing.RssiDbm = rssiDbm;
                        existing.FreqMhz = freqMhz;
                        existing.ReadCount++;
                        existing.LastSeenMs = nowMs;
                        return existing;
                    });

                LatestTag = rec;
            }
        }
    }

    public class GatePortalManager
    {
        public string ReaderIp = "192.168.0.201";
        public int ReaderPort = 9090;
        public string SerialNumber = "BRD-2204 (Impinj E710 — 4 Puertos SMA)";
        public bool Connected = false;
        public bool Scanning = false;
        public bool SimulationMode = false;
        public string StatusText = "Cable USB-Red detectado — Configura IP o toca Auto-Detectar";

        // USB-to-Ethernet Adapter Status
        public bool UsbLanConnected = false;
        public bool UsbLanReady = false;
        public string UsbLanAdapterName = "Ethernet";
        public string UsbLanDescription = "";
        public string UsbLanIp = "";

        public int[] AntennaPower = new int[] { 26, 26, 26, 26 }; // ANT 1..4 in dBm (10..33)
        public bool[] AntennaEnabled = new bool[] { false, false, false, false };
        public bool[] AntennaPlugged = new bool[] { false, false, false, false };
        public double[] AntennaReturnLossDb = new double[] { 0.0, 0.0, 0.0, 0.0 };
        public bool DetectingAntennas = false;
        private volatile int _currentSwrProbeAnt = -1;
        private readonly AutoResetEvent _swrReplyEvent = new AutoResetEvent(false);
        private int _invCycleCount = 0;
        private int _nextAutoProbeAnt = 0;
        public long LastRxMs = 0;
        public int[] AntennaReadCounts = new int[] { 0, 0, 0, 0 };
        public int[] AntennaLastRssi = new int[] { -75, -75, -75, -75 };

        public long TotalReads = 0;
        public int ReadsPerSec = 0;
        private int _readsInCurrentSec = 0;
        private long _lastRpsTickMs = 0;
        public TagReadRecord LatestTag = null;

        public int RssiThresholdDbm = -62;
        public int CorridorWidthCm = 150;

        // GPIO States on BRD-2204
        public bool Gpi1Internal = false; // IN1: Sensor IR Interno
        public bool Gpi2External = false; // IN2: Sensor IR Externo
        public bool Gpo1GreenLed = false; // O1: LED Verde (Salida OK)
        public bool Gpo2RedLed = false;   // O2: LED Rojo (Alarma)
        public bool Gpo3Siren = false;    // O3: Sirena 12V
        public bool Gpo4Aux = false;      // O4: Auxiliar

        public string LastDirection = "EN ESPERA";
        public string LastCalibrationReport = "Sin calibrar aún. Usa 'Auto-Calibrar Arco' para ajustar las 4 antenas automáticamente.";

        private TcpClient _tcpClient;
        private NetworkStream _netStream;
        private CancellationTokenSource _cts;
        private readonly object _txLock = new object();
        public ConcurrentDictionary<string, TagReadRecord> GateTags = new ConcurrentDictionary<string, TagReadRecord>();

        public void CheckUsbEthernetAdapter()
        {
            try
            {
                NetworkInterface bestNic = null;
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                        continue;

                    string desc = nic.Description ?? "";
                    string name = nic.Name ?? "";
                    if (desc.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        desc.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        desc.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    if (nic.OperationalStatus == OperationalStatus.Up)
                    {
                        bestNic = nic;
                        if (desc.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            desc.IndexOf("Realtek", StringComparison.OrdinalIgnoreCase) >= 0)
                            break;
                    }
                    else if (bestNic == null && (desc.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        bestNic = nic;
                    }
                }

                if (bestNic == null)
                {
                    UsbLanConnected = false;
                    UsbLanReady = false;
                    UsbLanDescription = "No detectado";
                    UsbLanIp = "Sin adaptador Ethernet";
                    if (Connected) DisconnectTcp();
                    StatusText = "Adaptador USB-Red desconectado — Lector y antenas apagados";
                    return;
                }

                UsbLanAdapterName = bestNic.Name;
                UsbLanDescription = bestNic.Description;
                UsbLanConnected = (bestNic.OperationalStatus == OperationalStatus.Up);
                if (!UsbLanConnected && Connected)
                {
                    DisconnectTcp();
                }

                var ipProps = bestNic.GetIPProperties();
                List<string> ipv4List = new List<string>();
                bool hasSubnet192 = false;
                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ipStr = ua.Address.ToString();
                        ipv4List.Add(ipStr);
                        if (ipStr.StartsWith("192.168.1.") || ipStr.StartsWith("192.168.0."))
                            hasSubnet192 = true;
                    }
                }

                UsbLanIp = ipv4List.Count > 0 ? string.Join(", ", ipv4List.ToArray()) : "Sin IP";
                UsbLanReady = UsbLanConnected && hasSubnet192;

                if (!Connected)
                {
                    if (!UsbLanConnected)
                        StatusText = "Adaptador " + UsbLanDescription + ": Cable RJ-45 desconectado o lector apagado";
                    else if (!UsbLanReady)
                        StatusText = "Cable USB-Red activo (" + UsbLanIp + ") — Toca 'Configurar IP del Cable USB-Red' para habilitar la subred 192.168.1.x";
                    else
                        StatusText = "Cable USB-Red listo (" + UsbLanIp + ") — Toca 'Auto-Detectar BRD-2204'";
                }
            }
            catch { }
        }

        public bool ConfigureUsbEthernetSubnet(out string msg)
        {
            CheckUsbEthernetAdapter();
            string nicName = string.IsNullOrEmpty(UsbLanAdapterName) ? "Ethernet" : UsbLanAdapterName;
            try
            {
                string cmdArgs = string.Format(
                    "/c netsh interface ip set address name=\"{0}\" static 192.168.1.200 255.255.255.0 none & netsh interface ip add address name=\"{0}\" 192.168.0.200 255.255.255.0",
                    nicName
                );
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = cmdArgs,
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process p = Process.Start(psi);
                if (p != null)
                {
                    p.WaitForExit(12000);
                }
                Thread.Sleep(1500);
                CheckUsbEthernetAdapter();
                if (UsbLanReady)
                {
                    string discMsg;
                    if (AutoDiscoverAndConnect(out discMsg))
                    {
                        msg = "Adaptador USB-Red configurado en 192.168.1.200 y " + discMsg;
                        return true;
                    }
                    msg = "Adaptador USB-Red configurado en 192.168.1.200 (" + discMsg + ")";
                    return true;
                }
                msg = "No se detectó el cambio a 192.168.1.200 (verifica aceptar el permiso de Windows en la notebook).";
                return false;
            }
            catch (Exception ex)
            {
                msg = "Configuración cancelada o pendiente de confirmación en Windows: " + ex.Message;
                return false;
            }
        }

        public bool AutoDiscoverAndConnect(out string msg)
        {
            if (Connected)
            {
                msg = StatusText;
                return true;
            }
            CheckUsbEthernetAdapter();
            // 1. Listen briefly for UDP multicast 230.1.1.168:8161 (GMulticast from reader.jar)
            try
            {
                using (UdpClient udp = new UdpClient())
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, 8161));
                    IPAddress mcast = IPAddress.Parse("230.1.1.168");
                    udp.JoinMulticastGroup(mcast);
                    udp.Client.ReceiveTimeout = 450;
                    IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = udp.Receive(ref ep);
                    if (data != null && data.Length > 0)
                    {
                        string ascii = Encoding.ASCII.GetString(data);
                        string foundIp = ExtractKeyVal(ascii, "IP") ?? ep.Address.ToString();
                        string foundPortStr = ExtractKeyVal(ascii, "PORT") ?? "9090";
                        int foundPort;
                        if (!int.TryParse(foundPortStr, out foundPort)) foundPort = 9090;
                        if (ConnectTcp(foundIp, foundPort, out msg))
                            return true;
                    }
                }
            }
            catch { }

            // 2. Parallel TCP probe of BRD-2204 IPs (including 192.168.0.201 and 192.168.1.201) and ports
            string[] candidateIps = new string[]
            {
                ReaderIp,
                "192.168.0.201",
                "192.168.1.201",
                "192.168.1.116",
                "192.168.1.168",
                "192.168.1.100",
                "192.168.1.250",
                "192.168.1.1",
                "192.168.0.116",
                "192.168.0.168"
            };
            int[] candidatePorts = new int[] { 9090, 8160, 6000, 4001 };

            string matchedIp = null;
            int matchedPort = 9090;
            object matchLock = new object();
            List<Task> tasks = new List<Task>();

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string cip in candidateIps)
            {
                if (string.IsNullOrWhiteSpace(cip)) continue;
                foreach (int cport in candidatePorts)
                {
                    string key = cip + ":" + cport;
                    if (!seen.Add(key)) continue;
                    string testIp = cip;
                    int testPort = cport;
                    tasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            using (TcpClient tc = new TcpClient())
                            {
                                var ar = tc.BeginConnect(testIp, testPort, null, null);
                                if (ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(700)) && tc.Connected)
                                {
                                    lock (matchLock)
                                    {
                                        if (matchedIp == null)
                                        {
                                            matchedIp = testIp;
                                            matchedPort = testPort;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }));
                }
            }

            try { Task.WaitAll(tasks.ToArray(), 950); } catch { }

            if (matchedIp != null)
            {
                return ConnectTcp(matchedIp, matchedPort, out msg);
            }

            if (UsbLanConnected && !UsbLanReady)
            {
                StatusText = "El cable USB-Red tiene IP " + UsbLanIp + ". Toca '1. Configurar IP del Cable USB-Red (192.168.1.200)' primero.";
            }
            else
            {
                StatusText = "No respondió BRD-2204 en 192.168.1.116 / .168 (Verifica que la fuente DC 12V del lector esté enchufada).";
            }
            msg = StatusText;
            return false;
        }

        private static string ExtractKeyVal(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int idx = text.IndexOf(key + "=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = text.IndexOf(key + ":", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int start = idx + key.Length + 1;
            int end = text.IndexOfAny(new char[] { ',', ';', '\r', '\n', ' ', '}' }, start);
            if (end < 0) end = text.Length;
            return text.Substring(start, end - start).Trim('\'', '"', ' ');
        }

        public bool ConnectTcp(string ip, int port, out string msg)
        {
            DisconnectTcp();
            CheckUsbEthernetAdapter();
            ReaderIp = ip;
            ReaderPort = port;
            try
            {
                _tcpClient = new TcpClient();
                var res = _tcpClient.BeginConnect(ip, port, null, null);
                bool success = res.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(1600));
                if (!success || !_tcpClient.Connected)
                {
                    _tcpClient.Close();
                    Connected = false;
                    if (UsbLanConnected && !UsbLanReady)
                        StatusText = "El cable USB-Red está en " + UsbLanIp + " — Toca 'Configurar IP del Cable USB-Red (192.168.1.200)' para conectar al BRD-2204.";
                    else
                        StatusText = "No se encontró BRD-2204 en " + ip + ":" + port + " (Verifica fuente 12V y cable RJ-45)";
                    msg = StatusText;
                    return false;
                }
                _tcpClient.EndConnect(res);
                _netStream = _tcpClient.GetStream();
                Connected = true;
                LastRxMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                SimulationMode = false;
                _cts = new CancellationTokenSource();
                Task.Run(() => TcpReadLoop(_cts.Token));

                // Initialize BRD-2204 GRP (0xEA) + CRP (0xAA) protocol:
                // 1. Stop ManualInventory (EA 00 04 01 57 00 BA)
                SendTcpFrame(BuildGrpFrame(0x01, 0x57, new byte[] { 0x00 }));
                Thread.Sleep(60);
                // 2. Query Reader Info (CRP 01 00) & Power (CRP 02 02)
                SendTcpFrame(BuildGrpPassCrpFrame(0x01, 0x00, null));
                Thread.Sleep(50);
                SendTcpFrame(BuildGrpPassCrpFrame(0x02, 0x02, null));
                Thread.Sleep(50);
                // 3. Detect physically connected antennas via SWR (CRP 0x05, 0x00) and start scan
                Task.Run(() => DetectPhysicalAntennas());

                StatusText = "BRD-2204 Conectado — Detectando antenas enchufadas (" + ip + ":" + port + ")...";
                msg = StatusText;
                return true;
            }
            catch (Exception ex)
            {
                Connected = false;
                StatusText = "Error TCP: " + ex.Message;
                msg = StatusText;
                return false;
            }
        }

        public void DetectPhysicalAntennas()
        {
            if (!Connected || DetectingAntennas) return;
            DetectingAntennas = true;
            bool wasScanning = Scanning;
            Scanning = false;
            try
            {
                StatusText = "Midiendo SWR en puertos SMA 1 a 4 para detectar antenas enchufadas...";
                SendTcpFrame(BuildGrpFrame(0x01, 0x57, new byte[] { 0x00 }));
                Thread.Sleep(350);
                // Ensure all 4 hardware ports are enabled in Ex10V2 (0x0F) before probing SWR
                SendTcpFrame(BuildGrpPassCrpFrame(0x02, 0x07, new byte[] { 0x0F }));
                Thread.Sleep(580);

                for (int i = 0; i < 4; i++)
                {
                    if (!Connected) break;
                    _currentSwrProbeAnt = i;
                    _swrReplyEvent.Reset();
                    // MsgCarrierWave (CRP 0x05, 0x00) triggers BRD-2204 hardware SWR macro (GRP 0x49)
                    byte mask = (byte)(1 << i);
                    SendTcpFrame(BuildGrpPassCrpFrame(0x05, 0x00, new byte[] { mask, 0x00 }));
                    if (!_swrReplyEvent.WaitOne(1250))
                    {
                        // Retry once if the Ex10V2 module was still settling
                        SendTcpFrame(BuildGrpPassCrpFrame(0x05, 0x00, new byte[] { mask, 0x00 }));
                        _swrReplyEvent.WaitOne(1250);
                    }
                    Thread.Sleep(150);
                }
                _currentSwrProbeAnt = -1;

                List<string> connList = new List<string>();
                for (int i = 0; i < 4; i++)
                {
                    AntennaEnabled[i] = AntennaPlugged[i];
                    if (AntennaPlugged[i])
                        connList.Add("ANT #" + (i + 1) + " (" + AntennaReturnLossDb[i].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " dB)");
                }

                SyncPowerToHardware();
                if (connList.Count > 0)
                {
                    StartInventory();
                    StatusText = "Antenas enchufadas (" + connList.Count + "/4): " + string.Join(", ", connList.ToArray());
                }
                else
                {
                    Scanning = false;
                    StatusText = "BRD-2204 Conectado (" + ReaderIp + ":" + ReaderPort + ") — Ninguna antena enchufada en bocas 1 a 4";
                }
            }
            catch { }
            finally
            {
                _currentSwrProbeAnt = -1;
                DetectingAntennas = false;
            }
        }

        public static ushort Crc16Crp(byte[] data, int offset, int length)
        {
            ushort crc = 0x0000;
            for (int i = offset; i < offset + length; i++)
            {
                crc ^= (ushort)(data[i] << 8);
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x8000) != 0)
                        crc = (ushort)((crc << 1) ^ 0x8005);
                    else
                        crc <<= 1;
                }
            }
            return crc;
        }

        public static byte[] BuildCrpFrame(byte category, byte mid, byte[] payload)
        {
            int payLen = payload != null ? payload.Length : 0;
            byte[] f = new byte[7 + payLen];
            f[0] = 0xAA;
            f[1] = category;
            f[2] = mid;
            f[3] = (byte)((payLen >> 8) & 0xFF);
            f[4] = (byte)(payLen & 0xFF);
            if (payLen > 0)
                Buffer.BlockCopy(payload, 0, f, 5, payLen);
            ushort crc = Crc16Crp(f, 1, 4 + payLen);
            f[5 + payLen] = (byte)((crc >> 8) & 0xFF);
            f[6 + payLen] = (byte)(crc & 0xFF);
            return f;
        }

        public static byte[] BuildGrpFrame(byte flag, byte cmd, byte[] payload)
        {
            int payLen = payload != null ? payload.Length : 0;
            int totalLen = 3 + 2 + payLen + 1;
            int lenField = 2 + payLen + 1;
            byte[] f = new byte[totalLen];
            f[0] = 0xEA;
            f[1] = (byte)((lenField >> 8) & 0xFF);
            f[2] = (byte)(lenField & 0xFF);
            f[3] = flag;
            f[4] = cmd;
            if (payLen > 0)
                Buffer.BlockCopy(payload, 0, f, 5, payLen);
            int sum = 0;
            for (int i = 0; i < totalLen - 1; i++)
                sum = (sum + f[i]) & 0xFF;
            f[totalLen - 1] = (byte)(((0xFF ^ sum) + 1) & 0xFF);
            return f;
        }

        public static byte[] BuildGrpPassCrpFrame(byte crpCat, byte crpMid, byte[] crpPayload)
        {
            byte[] crp = BuildCrpFrame(crpCat, crpMid, crpPayload);
            byte[] passPay = new byte[4 + crp.Length];
            passPay[0] = 0x01; // ProtocolVersion.CRP
            passPay[1] = 0x01; // Reader Address 1
            passPay[2] = 0x00;
            passPay[3] = 0x00;
            Buffer.BlockCopy(crp, 0, passPay, 4, crp.Length);
            return BuildGrpFrame(0x01, 0xA1, passPay);
        }

        public void DisconnectTcp()
        {
            Scanning = false;
            ReadsPerSec = 0;
            for (int i = 0; i < 4; i++)
            {
                AntennaPlugged[i] = false;
                AntennaEnabled[i] = false;
                AntennaReturnLossDb[i] = 0.0;
            }
            try
            {
                if (Connected && _netStream != null)
                    SendTcpFrame(BuildGrpFrame(0x01, 0x57, new byte[] { 0x00 }));
            }
            catch { }
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_netStream != null) _netStream.Close(); } catch { }
            try { if (_tcpClient != null) _tcpClient.Close(); } catch { }
            Connected = false;
        }

        public void SendTcpFrame(byte[] frame)
        {
            if (!Connected || _netStream == null || frame == null) return;
            lock (_txLock)
            {
                try
                {
                    _netStream.Write(frame, 0, frame.Length);
                    _netStream.Flush();
                }
                catch { }
            }
        }

        private byte GetEnabledAntennaMask()
        {
            byte mask = 0;
            for (int i = 0; i < 4; i++)
            {
                if (AntennaPlugged[i] && AntennaEnabled[i]) mask |= (byte)(1 << i);
            }
            return mask;
        }

        public void StartInventory()
        {
            if (!Connected) return;
            byte mask = GetEnabledAntennaMask();
            if (mask == 0)
            {
                Scanning = false;
                StatusText = "Conecta físicamente una antena a las bocas SMA 1–4 del BRD-2204";
                return;
            }
            Scanning = true;
            // Start CRP MsgTagInventory (0x02, 0x10) only on physically plugged & enabled antennas
            SendTcpFrame(BuildGrpPassCrpFrame(0x02, 0x10, new byte[] { mask, 0x01 }));
            StatusText = "BRD-2204 Leyendo en Vivo (" + ReaderIp + ":" + ReaderPort + ")";
        }

        public void StopInventory()
        {
            Scanning = false;
            ReadsPerSec = 0;
            if (!Connected) return;
            SendTcpFrame(BuildGrpFrame(0x01, 0x57, new byte[] { 0x00 }));
            Thread.Sleep(60);
            SendTcpFrame(BuildGrpPassCrpFrame(0x02, 0xFF, null));
            StatusText = "BRD-2204 en Espera (" + ReaderIp + ":" + ReaderPort + ")";
        }

        public void ClearTags()
        {
            GateTags.Clear();
            LatestTag = null;
            TotalReads = 0;
            ReadsPerSec = 0;
            for (int i = 0; i < 4; i++)
            {
                AntennaReadCounts[i] = 0;
                AntennaLastRssi[i] = -75;
            }
        }

        public void SyncPowerToHardware()
        {
            if (!Connected) return;
            bool wasScanning = Scanning;
            if (wasScanning)
            {
                SendTcpFrame(BuildGrpFrame(0x01, 0x57, new byte[] { 0x00 }));
                Thread.Sleep(60);
                SendTcpFrame(BuildGrpPassCrpFrame(0x02, 0xFF, null));
                Thread.Sleep(550);
            }
            SendTcpFrame(BuildGrpPassCrpFrame(0x02, 0x07, new byte[] { 0x0F }));
            Thread.Sleep(580);
            byte[] payload = new byte[]
            {
                0x01, (byte)AntennaPower[0],
                0x02, (byte)AntennaPower[1],
                0x03, (byte)AntennaPower[2],
                0x04, (byte)AntennaPower[3]
            };
            SendTcpFrame(BuildGrpPassCrpFrame(0x02, 0x01, payload));
            Thread.Sleep(580);
            if (wasScanning)
            {
                byte mask = GetEnabledAntennaMask();
                if (mask != 0)
                    SendTcpFrame(BuildGrpPassCrpFrame(0x02, 0x10, new byte[] { mask, 0x01 }));
            }
        }

        public void SetGpo(int port, bool on)
        {
            if (port == 1) Gpo1GreenLed = on;
            if (port == 2) Gpo2RedLed = on;
            if (port == 3) Gpo3Siren = on;
            if (port == 4) Gpo4Aux = on;

            if (Connected)
            {
                // BRD-2204 Gate_GpoConfig (0x63): EA 00 05 01 63 <port> <level> <chk>
                SendTcpFrame(BuildGrpFrame(0x01, 0x63, new byte[] { (byte)port, (byte)(on ? 1 : 0) }));
            }
        }

        public void RunSmartAutoCalibration(int corridorWidthCm, UsbGxReaderManager usbRef)
        {
            CorridorWidthCm = corridorWidthCm;
            int basePower;
            if (corridorWidthCm <= 120) basePower = 23;
            else if (corridorWidthCm <= 160) basePower = 25;
            else if (corridorWidthCm <= 220) basePower = 28;
            else if (corridorWidthCm <= 300) basePower = 30;
            else basePower = 32;

            int staticNoiseFloor = -66;
            if (usbRef != null && usbRef.LatestTag != null)
            {
                staticNoiseFloor = Math.Min(-58, usbRef.LatestTag.RssiDbm - 6);
            }

            AntennaPower[0] = Math.Min(33, basePower + 1); // ANT 1 Superior Izq
            AntennaPower[1] = Math.Max(15, basePower - 1); // ANT 2 Inferior Izq (menos rebote de piso)
            AntennaPower[2] = Math.Min(33, basePower + 1); // ANT 3 Superior Der
            AntennaPower[3] = Math.Max(15, basePower - 1); // ANT 4 Inferior Der (menos rebote de piso)
            RssiThresholdDbm = staticNoiseFloor + 4;

            if (Connected)
            {
                SyncPowerToHardware();
            }

            LastCalibrationReport = string.Format(
                "Auto-Calibración completada para pasillo de {0} cm: ANT1={1}dBm (Sup.Izq), ANT2={2}dBm (Inf.Izq), ANT3={3}dBm (Sup.Der), ANT4={4}dBm (Inf.Der) | Corte RSSI Anti-Rebote: {5} dBm.",
                corridorWidthCm, AntennaPower[0], AntennaPower[1], AntennaPower[2], AntennaPower[3], RssiThresholdDbm
            );
        }

        private async Task TcpReadLoop(CancellationToken token)
        {
            byte[] rxBuf = new byte[4096];
            byte[] accum = new byte[8192];
            int accumLen = 0;

            while (!token.IsCancellationRequested && Connected)
            {
                try
                {
                    int n = await _netStream.ReadAsync(rxBuf, 0, rxBuf.Length, token);
                    if (n <= 0)
                    {
                        Connected = false;
                        Scanning = false;
                        StatusText = "BRD-2204 desconectado";
                        break;
                    }
                    if (accumLen + n > accum.Length) accumLen = 0;
                    Buffer.BlockCopy(rxBuf, 0, accum, accumLen, n);
                    accumLen += n;

                    int idx = 0;
                    while (idx <= accumLen - 6)
                    {
                        if (accum[idx] == 0xEA)
                        {
                            int lenField = (accum[idx + 1] << 8) | accum[idx + 2];
                            if (lenField < 3 || lenField > 1024)
                            {
                                idx++;
                                continue;
                            }
                            int frameLen = 3 + lenField;
                            if (idx + frameLen > accumLen)
                                break; // wait for rest of 0xEA frame

                            ProcessGateGrpFrame(accum, idx, frameLen);
                            idx += frameLen;
                        }
                        else if (idx <= accumLen - 9 && accum[idx] == 0x5A && accum[idx + 1] == 0x00 && accum[idx + 2] == 0x01)
                        {
                            int payLen = (accum[idx + 5] << 8) | accum[idx + 6];
                            if (payLen < 0 || payLen > 1024)
                            {
                                idx++;
                                continue;
                            }
                            int frameLen = 7 + payLen + 2;
                            if (idx + frameLen > accumLen)
                                break; // wait for rest of 0x5A frame

                            ProcessGateGxFrame(accum, idx, frameLen);
                            idx += frameLen;
                        }
                        else
                        {
                            idx++;
                        }
                    }

                    if (idx > 0)
                    {
                        int rem = accumLen - idx;
                        if (rem > 0) Buffer.BlockCopy(accum, idx, accum, 0, rem);
                        accumLen = rem;
                    }
                }
                catch
                {
                    if (!token.IsCancellationRequested)
                    {
                        Connected = false;
                        Scanning = false;
                        StatusText = "Conexión TCP cerrada con BRD-2204";
                    }
                    break;
                }
            }
        }

        private void ProcessGateGrpFrame(byte[] buf, int offset, int frameLen)
        {
            if (frameLen < 6) return;
            byte flag = buf[offset + 3];
            byte cmd = buf[offset + 4];
            int payStart = offset + 5;
            int payLen = frameLen - 6;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LastRxMs = nowMs;
            if (nowMs - _lastRpsTickMs >= 1000)
            {
                ReadsPerSec = _readsInCurrentSec;
                _readsInCurrentSec = 0;
                _lastRpsTickMs = nowMs;
            }

            if (cmd == 0xA3 && payLen >= 7)
            {
                // Gate_RxdTagData: [epcLen_hi, epcLen_lo, epcBytes..., portNum, portState, antenna, rssi_hi, rssi_lo, ...]
                int epcLen = (buf[payStart] << 8) | buf[payStart + 1];
                if (epcLen <= 0 || 2 + epcLen + 5 > payLen) return;

                StringBuilder sbEpc = new StringBuilder(epcLen * 2);
                for (int i = 0; i < epcLen; i++)
                    sbEpc.Append(buf[payStart + 2 + i].ToString("X2"));
                string epcHex = sbEpc.ToString();

                int metaPos = payStart + 2 + epcLen;
                int antId = buf[metaPos + 2];
                if (antId < 1 || antId > 4) antId = 1;
                if (!AntennaPlugged[antId - 1] || !AntennaEnabled[antId - 1]) return;

                short rssiTenths = (short)((buf[metaPos + 3] << 8) | buf[metaPos + 4]);
                int rssiDbm = (int)Math.Round(rssiTenths / 10.0);
                if (rssiDbm > -15) rssiDbm = -45;
                if (rssiDbm < -95) rssiDbm = -95;

                int rssiRaw = Math.Max(1, Math.Min(35, (int)Math.Round(((rssiDbm + 85) / 52.0) * 35.0)));

                TotalReads++;
                _readsInCurrentSec++;
                AntennaReadCounts[antId - 1]++;
                AntennaLastRssi[antId - 1] = rssiDbm;

                TagReadRecord rec = GateTags.AddOrUpdate(epcHex,
                    k => new TagReadRecord
                    {
                        EpcHex = epcHex,
                        EpcAscii = GxProtocol.HexToCleanAscii(epcHex),
                        TidHex = "",
                        ChipModel = GxProtocol.DecodeChipFromTid(epcHex),
                        Antenna = antId,
                        RssiRaw = rssiRaw,
                        RssiDbm = rssiDbm,
                        FreqMhz = 915.25,
                        ReadCount = 1,
                        FirstSeenMs = nowMs,
                        LastSeenMs = nowMs,
                        SourceDevice = "BRD-2204"
                    },
                    (k, existing) =>
                    {
                        existing.EpcHex = epcHex;
                        existing.EpcAscii = GxProtocol.HexToCleanAscii(epcHex);
                        existing.Antenna = antId;
                        existing.RssiRaw = rssiRaw;
                        existing.RssiDbm = rssiDbm;
                        existing.ReadCount++;
                        existing.LastSeenMs = nowMs;
                        return existing;
                    });

                LatestTag = rec;
            }
            else if (cmd == 0x41 && payLen >= 5)
            {
                // Power query reply: [0x00, p1, p2, p3, p4]
                for (int i = 0; i < 4 && (1 + i) < payLen; i++)
                {
                    int dbm = buf[payStart + 1 + i];
                    if (dbm >= 5 && dbm <= 33)
                        AntennaPower[i] = dbm;
                }
            }
            else if (cmd == 0x49 && payLen >= 3)
            {
                // SWR directional coupler report: [0x00, forward, backward, ...]
                int fwd = buf[payStart + 1];
                int bwd = buf[payStart + 2];
                int diff = Math.Max(0, fwd - bwd);
                int probeAnt = _currentSwrProbeAnt;
                if (probeAnt >= 0 && probeAnt < 4)
                {
                    AntennaReturnLossDb[probeAnt] = Math.Round(diff / 5.0, 1);
                    bool isPlugged = (fwd > 0 && diff >= 35);
                    AntennaPlugged[probeAnt] = isPlugged;
                    AntennaEnabled[probeAnt] = isPlugged;
                    _swrReplyEvent.Set();
                }
            }
            else if (cmd == 0x67 && payLen >= 2)
            {
                // Gate_RxdGpiTriggerState
                byte gpiPort = buf[payStart];
                byte level = buf[payStart + 1];
                if (gpiPort == 1)
                {
                    Gpi1Internal = (level == 0);
                    if (Gpi1Internal) LastDirection = Gpi2External ? "ENTRADA AL LOCAL" : "DETECTANDO PASO INTERNO (IN1)";
                }
                if (gpiPort == 2)
                {
                    Gpi2External = (level == 0);
                    if (Gpi2External) LastDirection = Gpi1Internal ? "SALIDA HACIA CALLE" : "DETECTANDO PASO EXTERNO (IN2)";
                }
            }
            else if (cmd == 0xA1 && payLen >= 9)
            {
                // Gate_PassingMessages inner CRP frame starts at payStart + 4 (0xAA)
                int crpStart = payStart + 4;
                if (buf[crpStart] == 0xAA)
                {
                    byte crpCat = buf[crpStart + 1];
                    byte crpMid = buf[crpStart + 2];
                    if (crpCat == 0x12 && crpMid == 0x01)
                    {
                        _invCycleCount++;
                        // Inventory cycle ended; probe next antenna port every 4th cycle and re-trigger
                        if (Scanning && Connected && !DetectingAntennas)
                        {
                            Task.Run(() =>
                            {
                                if (_invCycleCount % 4 == 0)
                                {
                                    int p = (_nextAutoProbeAnt++) % 4;
                                    _currentSwrProbeAnt = p;
                                    SendTcpFrame(BuildGrpPassCrpFrame(0x05, 0x00, new byte[] { (byte)(1 << p), 0x00 }));
                                    Thread.Sleep(680);
                                    _currentSwrProbeAnt = -1;
                                }
                                else
                                {
                                    Thread.Sleep(40);
                                }
                                if (Scanning && Connected && !DetectingAntennas)
                                {
                                    byte mask = GetEnabledAntennaMask();
                                    if (mask != 0)
                                        SendTcpFrame(BuildGrpPassCrpFrame(0x02, 0x10, new byte[] { mask, 0x01 }));
                                    else
                                        Scanning = false;
                                }
                            });
                        }
                    }
                }
            }
        }

        private void ProcessGateGxFrame(byte[] buf, int offset, int frameLen)
        {
            byte cat = buf[offset + 3];
            byte mid = buf[offset + 4];
            int len = (buf[offset + 5] << 8) | buf[offset + 6];
            int payStart = offset + 7;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowMs - _lastRpsTickMs >= 1000)
            {
                ReadsPerSec = _readsInCurrentSec;
                _readsInCurrentSec = 0;
                _lastRpsTickMs = nowMs;
            }

            if (cat == 0x12 && mid == 0x01)
            {
                // LogBaseEpcOver -> re-trigger inventory on enabled antennas
                if (Scanning && Connected)
                {
                    Task.Run(() =>
                    {
                        Thread.Sleep(35);
                        if (Scanning && Connected)
                        {
                            byte mask = GetEnabledAntennaMask();
                            SendTcpFrame(GxProtocol.BuildFrame(0x02, 0x10, new byte[] { 0x00, 0x00, 0x00, mask, 0x01 }));
                        }
                    });
                }
                return;
            }

            if (cat == 0x01 && mid == 0x00 && len > 6)
            {
                int snLen = (buf[payStart] << 8) | buf[payStart + 1];
                if (snLen > 0 && snLen < 32 && payStart + 2 + snLen <= offset + frameLen)
                {
                    SerialNumber = Encoding.ASCII.GetString(buf, payStart + 2, snLen).Trim('\0');
                }
            }
            else if (cat == 0x02 && mid == 0x02 && len >= 2)
            {
                for (int p = 0; p + 1 < len; p += 2)
                {
                    int ant = buf[payStart + p];
                    int dbm = buf[payStart + p + 1];
                    if (ant >= 1 && ant <= 4 && dbm >= 5 && dbm <= 33)
                        AntennaPower[ant - 1] = dbm;
                }
            }
            else if (cat == 0x11 && (mid == 0x00 || mid == 0x01) && len >= 2)
            {
                byte gpiPort = buf[payStart];
                byte level = buf[payStart + 1];
                if (gpiPort == 1)
                {
                    Gpi1Internal = (level == 0);
                    if (Gpi1Internal) LastDirection = Gpi2External ? "ENTRADA AL LOCAL" : "DETECTANDO PASO INTERNO (IN1)";
                }
                if (gpiPort == 2)
                {
                    Gpi2External = (level == 0);
                    if (Gpi2External) LastDirection = Gpi1Internal ? "SALIDA HACIA CALLE" : "DETECTANDO PASO EXTERNO (IN2)";
                }
            }
            else if (cat == 0x12 && mid == 0x00 && len >= 5)
            {
                int epcLen = (buf[payStart] << 8) | buf[payStart + 1];
                int endPos = offset + 7 + len;
                if (epcLen <= 0 || payStart + 2 + epcLen + 3 > endPos) return;

                StringBuilder sbEpc = new StringBuilder(epcLen * 2);
                for (int i = 0; i < epcLen; i++)
                    sbEpc.Append(buf[payStart + 2 + i].ToString("X2"));
                string epcHex = sbEpc.ToString();

                int pos = payStart + 2 + epcLen;
                int antId = buf[pos + 2];
                if (antId < 1 || antId > 4) antId = 1;
                pos += 3;

                int rssiRaw = 18;
                string tidHex = "";
                double freqMhz = 915.25;

                while (pos < endPos)
                {
                    byte pid = buf[pos++];
                    if (pid == 0x01 && pos < endPos)
                    {
                        rssiRaw = buf[pos++];
                    }
                    else if (pid == 0x02 && pos < endPos)
                    {
                        pos++;
                    }
                    else if (pid == 0x03 && pos + 1 < endPos)
                    {
                        int tidLen = (buf[pos] << 8) | buf[pos + 1];
                        pos += 2;
                        StringBuilder sbTid = new StringBuilder(tidLen * 2);
                        for (int k = 0; k < tidLen && (pos + k) < endPos; k++)
                            sbTid.Append(buf[pos + k].ToString("X2"));
                        tidHex = sbTid.ToString();
                        pos += tidLen;
                    }
                    else if (pid == 0x06 && pos + 1 < endPos)
                    {
                        pos += 2;
                    }
                    else if (pid == 0x07 && pos < endPos)
                    {
                        pos += 1;
                    }
                    else if (pid == 0x08 && pos + 3 < endPos)
                    {
                        int freqKhz = (buf[pos] << 24) | (buf[pos + 1] << 16) | (buf[pos + 2] << 8) | buf[pos + 3];
                        if (freqKhz > 800000 && freqKhz < 1000000)
                            freqMhz = Math.Round(freqKhz / 1000.0, 2);
                        pos += 4;
                    }
                    else
                    {
                        break;
                    }
                }

                int rssiDbm = -85 + (int)Math.Round((rssiRaw / 35.0) * 52.0);
                if (rssiDbm > -25) rssiDbm = -25;
                if (rssiDbm < -85) rssiDbm = -85;

                TotalReads++;
                _readsInCurrentSec++;
                AntennaReadCounts[antId - 1]++;
                AntennaLastRssi[antId - 1] = rssiDbm;

                string key = string.IsNullOrEmpty(tidHex) ? epcHex : (epcHex + "|" + tidHex);
                TagReadRecord rec = GateTags.AddOrUpdate(key,
                    k => new TagReadRecord
                    {
                        EpcHex = epcHex,
                        EpcAscii = GxProtocol.HexToCleanAscii(epcHex),
                        TidHex = tidHex,
                        ChipModel = GxProtocol.DecodeChipFromTid(tidHex),
                        Antenna = antId,
                        RssiRaw = rssiRaw,
                        RssiDbm = rssiDbm,
                        FreqMhz = freqMhz,
                        ReadCount = 1,
                        FirstSeenMs = nowMs,
                        LastSeenMs = nowMs,
                        SourceDevice = "BRD-2204"
                    },
                    (k, existing) =>
                    {
                        existing.EpcHex = epcHex;
                        existing.EpcAscii = GxProtocol.HexToCleanAscii(epcHex);
                        if (!string.IsNullOrEmpty(tidHex))
                        {
                            existing.TidHex = tidHex;
                            existing.ChipModel = GxProtocol.DecodeChipFromTid(tidHex);
                        }
                        existing.Antenna = antId;
                        existing.RssiRaw = rssiRaw;
                        existing.RssiDbm = rssiDbm;
                        existing.FreqMhz = freqMhz;
                        existing.ReadCount++;
                        existing.LastSeenMs = nowMs;
                        return existing;
                    });

                LatestTag = rec;
            }
        }
    }

    public class MainForm : Form
    {
        private NotifyIcon _trayIcon;
        private Label _lblStatus;
        private int _port = 8090;
        private TcpListener _tcpServer;
        private bool _running = true;
        private bool _allowExit = false;
        private readonly object _usbScanLock = new object();

        public static UsbGxReaderManager Dc06 = new UsbGxReaderManager { DeviceRole = "DC06", MaxPowerDbm = 27 };
        public static UsbGxReaderManager Dc01 = new UsbGxReaderManager { DeviceRole = "DC01", MaxPowerDbm = 25, PowerDbm = 16, SerialNumber = "BRD-DC01 (Esperando USB)" };
        public static GatePortalManager Gate = new GatePortalManager();

        public MainForm()
        {
            Text = "Zyntra RFID Lab — Servidor de Antenas y Arco UHF";
            Size = new Size(495, 290);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Color.FromArgb(15, 23, 42);
            ForeColor = Color.White;

            Icon appIcon = CreateZyntraIcon(new Size(32, 32));
            Icon trayIconHandle = CreateZyntraIcon(SystemInformation.SmallIconSize);
            this.Icon = appIcon;

            ContextMenu trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("Abrir Consola Web de Antenas (http://localhost:" + _port + ")", (s, e) => OpenBrowser());
            trayMenu.MenuItems.Add("Mostrar Panel Zyntra RFID Lab", (s, e) => RestoreFromTray());
            trayMenu.MenuItems.Add("Reconectar Antenas USB", (s, e) => Task.Run(() => ForceReconnectUsbDevices()));
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Salir de Zyntra RFID Lab", (s, e) => ExitApp());

            _trayIcon = new NotifyIcon
            {
                Icon = trayIconHandle,
                Text = "Zyntra RFID Lab — Activo",
                ContextMenu = trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => RestoreFromTray();

            EnsureShortcutsAndStartup();

            var lblTitle = new Label
            {
                Text = "⚡ Zyntra RFID Lab — Motor de Antenas Activo",
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                Location = new Point(20, 18),
                AutoSize = true
            };
            _lblStatus = new Label
            {
                Text = "Iniciando puente USB HID y servidor en http://localhost:" + _port + " ...",
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(203, 213, 225),
                Location = new Point(20, 50),
                Size = new Size(445, 72)
            };
            var btnOpen = new Button
            {
                Text = "🌐 Abrir Consola Web RFID",
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                BackColor = Color.FromArgb(2, 132, 199),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(20, 130),
                Size = new Size(255, 38),
                Cursor = Cursors.Hand
            };
            btnOpen.Click += (s, e) => OpenBrowser();

            var btnReconnect = new Button
            {
                Text = "🔄 Reconectar USB",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(285, 130),
                Size = new Size(175, 38),
                Cursor = Cursors.Hand
            };
            btnReconnect.Click += (s, e) => Task.Run(() => ForceReconnectUsbDevices());

            var btnHideTray = new Button
            {
                Text = "✅ Dejar Activo en Segundo Plano (Junto al Reloj)",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                BackColor = Color.FromArgb(16, 185, 129),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(20, 178),
                Size = new Size(440, 38),
                Cursor = Cursors.Hand
            };
            btnHideTray.Click += (s, e) => HideToTray(true);

            Controls.Add(lblTitle);
            Controls.Add(_lblStatus);
            Controls.Add(btnOpen);
            Controls.Add(btnReconnect);
            Controls.Add(btnHideTray);

            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    HideToTray(false);
                }
            };

            Shown += (s, e) =>
            {
                AutoUpdater.CleanupOldBinary();
                StartHttpServer();
                Task.Run(() =>
                {
                    Gate.CheckUsbEthernetAdapter();
                    AutoDetectUsbDevices();
                });
                StartUsbWatchdog();
                Task.Run(() =>
                {
                    string msg;
                    if (AutoUpdater.CheckForUpdate(out msg) && AutoUpdater.UpdateAvailable)
                    {
                        AutoUpdater.ApplySelfUpdate(out msg);
                    }
                });
            };

            FormClosing += (s, e) =>
            {
                if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    HideToTray(true);
                    return;
                }
                _running = false;
                Dc06.Disconnect();
                Dc01.Disconnect();
                Gate.DisconnectTcp();
                _trayIcon.Visible = false;
            };
        }

        private void HideToTray(bool showBalloon)
        {
            ShowInTaskbar = false;
            Hide();
            if (showBalloon && _trayIcon != null)
            {
                _trayIcon.ShowBalloonTip(
                    2500,
                    "Zyntra RFID Lab Activo",
                    "El motor de antenas USB sigue corriendo junto al reloj (Puerto " + _port + ").",
                    ToolTipIcon.Info
                );
            }
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            Activate();
        }

        private void StartUsbWatchdog()
        {
            Task.Run(() =>
            {
                int tick = 0;
                while (_running)
                {
                    try
                    {
                        Thread.Sleep(1500);
                        tick++;
                        if (Dc06.Connected && !UsbGxReaderManager.IsDevicePathPresent(Dc06.DevicePath))
                        {
                            Dc06.HandlePhysicalDisconnect();
                            UpdateWindowLabel();
                        }
                        if (Dc01.Connected && !UsbGxReaderManager.IsDevicePathPresent(Dc01.DevicePath))
                        {
                            Dc01.HandlePhysicalDisconnect();
                            UpdateWindowLabel();
                        }
                        if (!Dc06.Connected || !Dc01.Connected)
                        {
                            var paths = UsbGxReaderManager.FindAllGxHidPaths();
                            if ((!Dc06.Connected && !Dc01.Connected && paths.Count >= 1) ||
                                ((!Dc06.Connected || !Dc01.Connected) && paths.Count >= 2))
                            {
                                AutoDetectUsbDevices();
                            }
                        }
                        Gate.CheckUsbEthernetAdapter();
                        if (Gate.Connected && !Gate.DetectingAntennas)
                        {
                            long idleMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Gate.LastRxMs;
                            if (idleMs > 9000)
                            {
                                Gate.DisconnectTcp();
                                UpdateWindowLabel();
                            }
                            else if (idleMs > 4500)
                            {
                                bool anyPlugged = Gate.AntennaPlugged[0] || Gate.AntennaPlugged[1] || Gate.AntennaPlugged[2] || Gate.AntennaPlugged[3];
                                if (!anyPlugged && tick % 3 == 0)
                                {
                                    // Auto-detect when an antenna is plugged in while idle with 0 antennas
                                    Task.Run(() => Gate.DetectPhysicalAntennas());
                                }
                                else
                                {
                                    // Lightweight heartbeat when idle so LastRxMs refreshes if reader is still alive
                                    Gate.SendTcpFrame(GatePortalManager.BuildGrpFrame(0x01, 0xF3, new byte[] { 0x00, 0x00, 0x00, 0x00 }));
                                }
                            }
                        }
                        if (tick % 2 == 0)
                        {
                            if (!Gate.Connected && Gate.UsbLanConnected && Gate.UsbLanReady)
                            {
                                string gMsg;
                                if (Gate.AutoDiscoverAndConnect(out gMsg))
                                    UpdateWindowLabel();
                            }
                        }
                    }
                    catch { }
                }
            });
        }

        private void ForceReconnectUsbDevices()
        {
            lock (_usbScanLock)
            {
                int savedPwr6 = Dc06.PowerDbm;
                Dc06.Disconnect();
                Dc01.Disconnect();
                Thread.Sleep(250);
                Dc06.PowerDbm = savedPwr6;
                AutoDetectUsbDevices();
            }
        }

        private static string GetUsbRoleConfigPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZyntraRfidLab");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "usb_roles.txt");
        }

        private static Dictionary<string, string> LoadUsbRoleMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "2BC972ACC000", "DC01" },
                { "BRDC600002328A000114", "DC01" },
                { "2BC978ECC000", "DC06" },
                { "BRDC600002328A000282", "DC06" }
            };
            try
            {
                string cfg = GetUsbRoleConfigPath();
                if (File.Exists(cfg))
                {
                    foreach (string line in File.ReadAllLines(cfg))
                    {
                        string[] parts = line.Split('=');
                        if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
                            map[parts[0].Trim()] = parts[1].Trim().ToUpperInvariant();
                    }
                }
            }
            catch { }
            return map;
        }

        private static void SaveUsbRoleMapping(string key, string role)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            try
            {
                var map = LoadUsbRoleMap();
                map[key.Trim()] = role.Trim().ToUpperInvariant();
                var lines = new List<string>();
                foreach (var kv in map) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(GetUsbRoleConfigPath(), lines.ToArray());
            }
            catch { }
        }

        private static string DetermineRoleForPath(string devPath, Dictionary<string, string> roleMap)
        {
            if (!string.IsNullOrEmpty(devPath))
            {
                foreach (var kv in roleMap)
                {
                    if (devPath.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                        return kv.Value;
                }
            }
            return "";
        }

        private void AutoDetectUsbDevices()
        {
            lock (_usbScanLock)
            {
                var paths = UsbGxReaderManager.FindAllGxHidPaths();
                if (paths.Count == 0)
                {
                    if (Dc06.Connected) Dc06.HandlePhysicalDisconnect();
                    if (Dc01.Connected) Dc01.HandlePhysicalDisconnect();
                    UpdateWindowLabel();
                    return;
                }

                var roleMap = LoadUsbRoleMap();
                string pathForDc06 = null;
                string pathForDc01 = null;
                var unassigned = new List<string>();

                foreach (string p in paths)
                {
                    string mappedRole = DetermineRoleForPath(p, roleMap);
                    if (mappedRole == "DC01" && pathForDc01 == null) pathForDc01 = p;
                    else if (mappedRole == "DC06" && pathForDc06 == null) pathForDc06 = p;
                    else unassigned.Add(p);
                }

                foreach (string p in unassigned)
                {
                    if (pathForDc06 == null) pathForDc06 = p;
                    else if (pathForDc01 == null) pathForDc01 = p;
                }

                if (pathForDc06 != null && (!Dc06.Connected || !string.Equals(Dc06.DevicePath, pathForDc06, StringComparison.OrdinalIgnoreCase)))
                {
                    if (Dc06.PowerDbm <= 0) Dc06.PowerDbm = 27;
                    if (Dc06.Connect(pathForDc06))
                    {
                        Thread.Sleep(260);
                        string snRole = DetermineRoleForPath(Dc06.SerialNumber, roleMap);
                        if (snRole == "DC01" && !Dc01.Connected)
                        {
                            SaveUsbRoleMapping(pathForDc06, "DC01");
                            Dc06.Disconnect();
                            Dc06.SerialNumber = "BRD-DC06 (Esperando USB)";
                            if (Dc01.PowerDbm <= 0) Dc01.PowerDbm = 16;
                            if (Dc01.Connect(pathForDc06))
                            {
                                Dc01.StartInventory();
                            }
                        }
                        else
                        {
                            Dc06.StartInventory();
                        }
                    }
                }
                else if (pathForDc06 == null && Dc06.Connected)
                {
                    Dc06.Disconnect();
                    Dc06.SerialNumber = "BRD-DC06 (Esperando USB)";
                }

                if (pathForDc01 != null && (!Dc01.Connected || !string.Equals(Dc01.DevicePath, pathForDc01, StringComparison.OrdinalIgnoreCase)))
                {
                    if (Dc01.PowerDbm <= 0) Dc01.PowerDbm = 16;
                    if (Dc01.Connect(pathForDc01))
                    {
                        Dc01.StartInventory();
                    }
                }
                else if (pathForDc01 == null && Dc01.Connected)
                {
                    Dc01.Disconnect();
                    Dc01.SerialNumber = "BRD-DC01 (Esperando USB)";
                }

                UpdateWindowLabel();
            }
        }

        private void SwapUsbRoles()
        {
            lock (_usbScanLock)
            {
                if (Dc06.Connected && !Dc01.Connected && !string.IsNullOrEmpty(Dc06.DevicePath))
                {
                    SaveUsbRoleMapping(Dc06.DevicePath, "DC01");
                    if (!string.IsNullOrEmpty(Dc06.SerialNumber)) SaveUsbRoleMapping(Dc06.SerialNumber, "DC01");
                }
                else if (Dc01.Connected && !Dc06.Connected && !string.IsNullOrEmpty(Dc01.DevicePath))
                {
                    SaveUsbRoleMapping(Dc01.DevicePath, "DC06");
                    if (!string.IsNullOrEmpty(Dc01.SerialNumber)) SaveUsbRoleMapping(Dc01.SerialNumber, "DC06");
                }
                else if (Dc06.Connected && Dc01.Connected)
                {
                    string p6 = Dc06.DevicePath;
                    string p1 = Dc01.DevicePath;
                    SaveUsbRoleMapping(p6, "DC01");
                    SaveUsbRoleMapping(p1, "DC06");
                }
                ForceReconnectUsbDevices();
            }
        }

        private void UpdateWindowLabel()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(UpdateWindowLabel));
                return;
            }
            _lblStatus.Text = string.Format(
                "• BRD-DC06 (USB): {0} ({1})\n• BRD-DC01 (Pad): {2} | • BRD-2204 (Red): {3}\n• Consola Web: http://localhost:{4}",
                Dc06.Connected ? "CONECTADA" : "No detectada",
                Dc06.Scanning ? "Leyendo a " + Dc06.PowerDbm + " dBm" : "En espera",
                Dc01.Connected ? "CONECTADO (" + Dc01.PowerDbm + " dBm)" : "En espera",
                Gate.Connected ? "CONECTADO (" + Gate.ReaderIp + ")" : (Gate.UsbLanConnected ? "Cable USB-Red Activo" : "En espera"),
                _port
            );
            if (_trayIcon != null)
            {
                if (Gate.Connected)
                    _trayIcon.Text = "Zyntra RFID Lab — Arco BRD-2204 Conectado";
                else if (Dc06.Connected && Dc01.Connected)
                    _trayIcon.Text = "Zyntra RFID Lab — DC06 + PAD DC01";
                else if (Dc06.Connected)
                    _trayIcon.Text = "Zyntra RFID Lab — BRD-DC06 (" + Dc06.PowerDbm + " dBm)";
                else if (Dc01.Connected)
                    _trayIcon.Text = "Zyntra RFID Lab — PAD DC01 (" + Dc01.PowerDbm + " dBm)";
                else
                    _trayIcon.Text = "Zyntra RFID Lab — Activo";
            }
        }

        private static void EnsureShortcutsAndStartup()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (rk != null)
                    {
                        rk.SetValue("ZyntraRfidLab", "\"" + exePath + "\"");
                    }
                }
                string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktopDir) && Directory.Exists(desktopDir))
                {
                    string lnkPath = Path.Combine(desktopDir, "Zyntra RFID Lab.lnk");
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType != null)
                    {
                        object shell = Activator.CreateInstance(shellType);
                        object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                        if (sc != null)
                        {
                            Type t = sc.GetType();
                            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exePath });
                            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(exePath) ?? "" });
                            t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Zyntra RFID Lab — Control de Antenas y Arco UHF" });
                            t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { exePath + ",0" });
                            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                        }
                    }
                }
            }
            catch { }
        }

        private static Icon CreateZyntraIcon(Size targetSize)
        {
            try
            {
                int w = Math.Max(16, targetSize.Width);
                int h = Math.Max(16, targetSize.Height);
                const string b64 = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAQNSURBVFhH7Vfbj19TFC6t+6VVKo1rXFqdIgRFUxHVtCqieNCISwWNqLoED148TJtKEUVcEnFpNKqS+gvEo4iISCQIicSlQqKYzoz5zZyzv/V9S9Z0nzrndH7D1MybL/nyO/s7a+81e5219l4zbdr/qAHAjZI+MrP1ZVmuNrPVkkbp7se17ScdKaUHJJUAXgPQS7LX3XslbXD3uWEj6aj2vElFX1/fzLYWcPdDzOwDkr+Y2WNmtsbM7jSzu+ssy3Ktmd2bebuk2wDc5O4Ht9ecEAYGBo4n6QEAu8zsiyCAL+s0s/j9CsAnZpbC3sxubq93QEgpLYldt/WxkFJaRPI3AKti3N/fP1vS/ZLOatt2haQVAK6K8Acr3d0PcvfpNc4IVu9TSleSLCOJKw3AphyN9yttXLj7kaPx3hvyd7uRZPAdSefGPABLzYxmdmttrVkkvyf5tZnd0nA0HgBcC2BZtcOIgqRjI1EruvtMScdk+2WRJ2Z2R7VGhD4737Fz587pDQd1SJpHcntkbkppsaTFRVH0FEWxUNKlwbIsL2rPqwDg6hziuypNUiTujwC2N63HQCRLDveHAF4G8EqdkuJ3Y/1b1+ZGlML52kobHBycQ/InANua1uOgKIp5be2fAGB5dn5fpQ0NDc0l+TPJrU3rCcDMHozvBuCGoKTgGXUbACuy8/WVJqknyo/k63XbCQPAZpKfmdm6oKR1kQ+19yuz84db81aRfL6uTToAXJedP1pp8c3d/dCm5RTA3c8ko8zt8UpLKV1mZn/GAdS0ngJIOiyldHk1TildIimqZ8tYFTJpGBkZOZvkc3H0Vpq7X8i9eCqPZ5jZPZIeykn8ppl95+4nNBabKIaHh08zsz8AbK12Ken8+A6V80C7k7RU0jVRIfmPeNvdj24sOBFIOt3M+gC8UNMWxmUD4Omm9dioXV5xlF8vaVHbZkxE3ZtZP8ktNe0ckiMkNzetR9/NBvCGpDg/9qOZvZer55v23P0Qp6KZDZB8pqV1SD7ZtP4bUYrV9R07z1rcrLOir4xOKu6N9rwGiqJYEGVV32Uuv0GSm2JcluV57n5FSqnOJVEhQUlRmmskbZN0asNBN0TDCeBVkhH2fbvMeRDaxhi7++EANkiKS6sbXzKzj/MFt7zhqBtSShdXTciePXtmhdbpdE7JjegTbft/gyjfttYVuY4fqRqKTqdzMsldERVJR0iaI+nEboyQ51b+wMuugqSTSH6es/YHAL8DiCZzH81sN4BfM3dX0YvDqL3ehBHdjKQLolOKfi+SLq7a3CX1xLO7L5A0vyiK+fEcnQ/JT+O5vV4g2rlgW58SRI8Q5weAZyuSHADwbf04nzLEDSlpZfQNAOJoXknyLZIv/uf/kiYTfwHgIfyipOKABgAAAABJRU5ErkJggg==";
                byte[] bytes = Convert.FromBase64String(b64);
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Bitmap srcBmp = new Bitmap(ms))
                using (Bitmap scaled = new Bitmap(w, h))
                {
                    using (Graphics g = Graphics.FromImage(scaled))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.Clear(Color.Black);
                        g.DrawImage(srcBmp, new Rectangle(0, 0, w, h));
                    }
                    return Icon.FromHandle(scaled.GetHicon());
                }
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        private void OpenBrowser()
        {
            try
            {
                Process.Start("http://localhost:" + _port);
            }
            catch { }
        }

        private void ExitApp()
        {
            _allowExit = true;
            _running = false;
            Dc06.Disconnect();
            Dc01.Disconnect();
            Gate.DisconnectTcp();
            _trayIcon.Visible = false;
            Application.Exit();
        }

        private void StartHttpServer()
        {
            Task.Run(() =>
            {
                try
                {
                    _tcpServer = new TcpListener(IPAddress.Any, _port);
                    _tcpServer.Start();
                    while (_running)
                    {
                        TcpClient client = _tcpServer.AcceptTcpClient();
                        ThreadPool.QueueUserWorkItem(_ => HandleTcpHttpClient(client));
                    }
                }
                catch { }
            });
        }

        private void HandleTcpHttpClient(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = 5000;
                    stream.WriteTimeout = 5000;
                    byte[] buffer = new byte[16384];
                    int totalRead = 0;
                    int headerEnd = -1;
                    while (totalRead < buffer.Length)
                    {
                        int n = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                        if (n <= 0) break;
                        totalRead += n;
                        string s = Encoding.ASCII.GetString(buffer, 0, totalRead);
                        headerEnd = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (headerEnd >= 0)
                        {
                            // Check Content-Length
                            int clIdx = s.IndexOf("Content-Length:", StringComparison.OrdinalIgnoreCase);
                            if (clIdx >= 0 && clIdx < headerEnd)
                            {
                                int lineEnd = s.IndexOf("\r\n", clIdx, StringComparison.Ordinal);
                                string clStr = s.Substring(clIdx + 15, lineEnd - (clIdx + 15)).Trim();
                                int contentLen;
                                if (int.TryParse(clStr, out contentLen))
                                {
                                    int bodyStart = headerEnd + 4;
                                    while (totalRead - bodyStart < contentLen && totalRead < buffer.Length)
                                    {
                                        int more = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                                        if (more <= 0) break;
                                        totalRead += more;
                                    }
                                }
                            }
                            break;
                        }
                    }
                    if (totalRead <= 0) return;
                    string fullReq = Encoding.UTF8.GetString(buffer, 0, totalRead);
                    int firstLineEnd = fullReq.IndexOf("\r\n", StringComparison.Ordinal);
                    if (firstLineEnd < 0) return;
                    string[] reqParts = fullReq.Substring(0, firstLineEnd).Split(' ');
                    if (reqParts.Length < 2) return;
                    string method = reqParts[0].ToUpperInvariant();
                    string rawUrl = reqParts[1];
                    string path = rawUrl.Split('?')[0].ToLowerInvariant();
                    string body = "";
                    int bIdx = fullReq.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (bIdx >= 0 && bIdx + 4 < fullReq.Length)
                        body = fullReq.Substring(bIdx + 4);

                    if (method == "OPTIONS")
                    {
                        SendTcpResponse(stream, "204 No Content", "text/plain", new byte[0]);
                        return;
                    }

                    if (path == "/api/state")
                    {
                        SendTcpJson(stream, BuildStateJson());
                        return;
                    }
                    if (path == "/api/usb/toggle" && method == "POST")
                    {
                        string dev = ExtractJsonStr(body, "device") ?? "DC06";
                        var mgr = (dev == "DC01") ? Dc01 : Dc06;
                        if (mgr.Scanning) mgr.StopInventory();
                        else mgr.StartInventory();
                        UpdateWindowLabel();
                        SendTcpJson(stream, BuildStateJson());
                        return;
                    }
                    if (path == "/api/usb/clear" && method == "POST")
                    {
                        string dev = ExtractJsonStr(body, "device") ?? "DC06";
                        var mgr = (dev == "DC01") ? Dc01 : Dc06;
                        mgr.Tags.Clear();
                        mgr.LatestTag = null;
                        mgr.TotalReads = 0;
                        SendTcpJson(stream, BuildStateJson());
                        return;
                    }
                    if (path == "/api/usb/power" && method == "POST")
                    {
                        string dev = ExtractJsonStr(body, "device") ?? "DC06";
                        int dbm = ExtractJsonInt(body, "dbm", 25);
                        var mgr = (dev == "DC01") ? Dc01 : Dc06;
                        mgr.SetPower(dbm);
                        UpdateWindowLabel();
                        SendTcpJson(stream, BuildStateJson());
                        return;
                    }
                    if (path == "/api/usb/beep" && method == "POST")
                    {
                        string dev = ExtractJsonStr(body, "device") ?? "DC06";
                        var mgr = (dev == "DC01") ? Dc01 : Dc06;
                        mgr.TriggerBeep();
                        SendTcpJson(stream, "{\"ok\":true}");
                        return;
                    }
                    if (path == "/api/usb/write" && method == "POST")
                    {
                        string dev = ExtractJsonStr(body, "device") ?? "DC06";
                        string val = ExtractJsonStr(body, "value") ?? "";
                        bool isAscii = !body.Contains("\"isAscii\":false");
                        var mgr = (dev == "DC01") ? Dc01 : Dc06;
                        string msg;
                        bool ok = mgr.WriteEpcTag(val, isAscii, out msg);
                        SendTcpJson(stream, "{\"ok\":" + (ok ? "true" : "false") + ",\"message\":\"" + EscapeJson(msg) + "\"}");
                        return;
                    }
                    if (path == "/api/usb/reconnect" && method == "POST")
                    {
                        ForceReconnectUsbDevices();
                        SendTcpJson(stream, BuildStateJson());
                        return;
                    }
                    if (path == "/api/usb/swap" && method == "POST")
                    {
                        SwapUsbRoles();
                        SendTcpJson(stream, BuildStateJson());
                        return;
                    }
                    if (path == "/api/gate/setup-usb-lan" && method == "POST")
                    {
                        string msg;
                        bool ok = Gate.ConfigureUsbEthernetSubnet(out msg);
                        UpdateWindowLabel();
                        SendTcpJson(stream, "{\"ok\":" + (ok ? "true" : "false") + ",\"message\":\"" + EscapeJson(msg) + "\"}");
                        return;
                    }
                    if (path == "/api/gate/autodiscover" && method == "POST")
                    {
                        string msg;
                        bool ok = Gate.AutoDiscoverAndConnect(out msg);
                        UpdateWindowLabel();
                        SendTcpJson(stream, "{\"ok\":" + (ok ? "true" : "false") + ",\"message\":\"" + EscapeJson(msg) + "\"}");
                        return;
                    }
                    if (path == "/api/gate/toggle" && method == "POST")
                    {
                        if (Gate.Scanning) Gate.StopInventory();
                        else Gate.StartInventory();
                        UpdateWindowLabel();
                        SendTcpJson(stream, BuildStateJson());
                        return;
                    }
                    if (path == "/api/gate/clear" && method == "POST")
                    {
                        Gate.ClearTags();
                        SendTcpJson(stream, BuildStateJson());
                        return;
                    }
                    if (path == "/api/gate/connect" && method == "POST")
                    {
                        string ip = ExtractJsonStr(body, "ip") ?? "192.168.1.116";
                        int port = ExtractJsonInt(body, "port", 9090);
                        string msg;
                        bool ok = Gate.ConnectTcp(ip, port, out msg);
                        UpdateWindowLabel();
                        SendTcpJson(stream, "{\"ok\":" + (ok ? "true" : "false") + ",\"message\":\"" + EscapeJson(msg) + "\"}");
                        return;
                    }
                    if (path == "/api/gate/power" && method == "POST")
                    {
                        int ant = ExtractJsonInt(body, "antenna", 1);
                        int dbm = ExtractJsonInt(body, "dbm", 26);
                        if (ant >= 1 && ant <= 4)
                        {
                            Gate.AntennaPower[ant - 1] = Math.Max(10, Math.Min(33, dbm));
                            Gate.SyncPowerToHardware();
                        }
                        SendTcpJson(stream, BuildStateJson());
                        return;
                    }
                    if (path == "/api/gate/toggle-ant" && method == "POST")
                    {
                        int ant = ExtractJsonInt(body, "antenna", 1);
                        if (ant >= 1 && ant <= 4 && Gate.Connected && Gate.AntennaPlugged[ant - 1])
                        {
                            Gate.AntennaEnabled[ant - 1] = !Gate.AntennaEnabled[ant - 1];
                            Gate.SyncPowerToHardware();
                        }
                        SendTcpJson(stream, BuildStateJson());
                        return;
                    }
                    if (path == "/api/gate/detect-antennas" && method == "POST")
                    {
                        Task.Run(() => Gate.DetectPhysicalAntennas());
                        SendTcpJson(stream, BuildStateJson());
                        return;
                    }
                    if (path == "/api/gate/calibrate" && method == "POST")
                    {
                        int widthCm = ExtractJsonInt(body, "widthCm", 150);
                        Gate.RunSmartAutoCalibration(widthCm, Dc06);
                        SendTcpJson(stream, BuildStateJson());
                        return;
                    }
                    if (path == "/api/gate/gpo" && method == "POST")
                    {
                        int port = ExtractJsonInt(body, "port", 1);
                        bool state = body.Contains("\"on\":true");
                        Gate.SetGpo(port, state);
                        SendTcpJson(stream, BuildStateJson());
                        return;
                    }
                    if (path == "/api/update/check" && method == "POST")
                    {
                        string msg;
                        bool avail = AutoUpdater.CheckForUpdate(out msg);
                        if (avail)
                        {
                            AutoUpdater.ApplySelfUpdate(out msg);
                        }
                        SendTcpJson(stream, "{\"ok\":true,\"updateAvailable\":" + (avail ? "true" : "false") + ",\"version\":\"" + AutoUpdater.CurrentVersion + "\",\"remoteVersion\":\"" + AutoUpdater.RemoteVersion + "\",\"message\":\"" + EscapeJson(msg) + "\"}");
                        return;
                    }

                    string uiDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui");
                    if (path == "/" || path == "/index.html")
                    {
                        string indexFile = Path.Combine(uiDir, "index.html");
                        byte[] bytes = File.ReadAllBytes(indexFile);
                        SendTcpResponse(stream, "200 OK", "text/html; charset=UTF-8", bytes);
                        return;
                    }
                    if (path == "/logo.png")
                    {
                        string logoFile = Path.Combine(uiDir, "logo.png");
                        if (File.Exists(logoFile))
                        {
                            byte[] bytes = File.ReadAllBytes(logoFile);
                            SendTcpResponse(stream, "200 OK", "image/png", bytes);
                            return;
                        }
                    }
                    if (path == "/favicon.ico")
                    {
                        string icoFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zyntra.ico");
                        if (File.Exists(icoFile))
                        {
                            byte[] bytes = File.ReadAllBytes(icoFile);
                            SendTcpResponse(stream, "200 OK", "image/x-icon", bytes);
                            return;
                        }
                    }

                    SendTcpResponse(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("404 Not Found"));
                }
            }
            catch { }
        }

        private static void SendTcpJson(NetworkStream stream, string json)
        {
            SendTcpResponse(stream, "200 OK", "application/json; charset=UTF-8", Encoding.UTF8.GetBytes(json));
        }

        private static void SendTcpResponse(NetworkStream stream, string status, string contentType, byte[] body)
        {
            string headers =
                "HTTP/1.1 " + status + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: Content-Type, Accept, Access-Control-Request-Private-Network\r\n" +
                "Access-Control-Allow-Private-Network: true\r\n" +
                "Cache-Control: no-cache\r\n" +
                "Connection: close\r\n\r\n";
            byte[] hBytes = Encoding.ASCII.GetBytes(headers);
            stream.Write(hBytes, 0, hBytes.Length);
            if (body.Length > 0)
                stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private string BuildStateJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"dc06\":").Append(SerializeUsbManager(Dc06)).Append(",");
            sb.Append("\"dc01\":").Append(SerializeUsbManager(Dc01)).Append(",");
            sb.Append("\"gate\":{");
            sb.Append("\"ip\":\"").Append(EscapeJson(Gate.ReaderIp)).Append("\",");
            sb.Append("\"port\":").Append(Gate.ReaderPort).Append(",");
            sb.Append("\"serial\":\"").Append(EscapeJson(Gate.SerialNumber)).Append("\",");
            sb.Append("\"connected\":").Append(Gate.Connected ? "true" : "false").Append(",");
            sb.Append("\"scanning\":").Append(Gate.Scanning ? "true" : "false").Append(",");
            sb.Append("\"status\":\"").Append(EscapeJson(Gate.StatusText)).Append("\",");
            sb.Append("\"usbLanConnected\":").Append(Gate.UsbLanConnected ? "true" : "false").Append(",");
            sb.Append("\"usbLanReady\":").Append(Gate.UsbLanReady ? "true" : "false").Append(",");
            sb.Append("\"usbLanDesc\":\"").Append(EscapeJson(Gate.UsbLanDescription)).Append("\",");
            sb.Append("\"usbLanIp\":\"").Append(EscapeJson(Gate.UsbLanIp)).Append("\",");
            sb.Append("\"totalReads\":").Append(Gate.TotalReads).Append(",");
            sb.Append("\"readsPerSec\":").Append(Gate.ReadsPerSec).Append(",");
            sb.Append("\"lastDirection\":\"").Append(EscapeJson(Gate.LastDirection)).Append("\",");
            sb.Append("\"rssiThreshold\":").Append(Gate.RssiThresholdDbm).Append(",");
            sb.Append("\"corridorWidthCm\":").Append(Gate.CorridorWidthCm).Append(",");
            sb.Append("\"calibrationReport\":\"").Append(EscapeJson(Gate.LastCalibrationReport)).Append("\",");
            sb.Append("\"powers\":[").Append(Gate.AntennaPower[0]).Append(",").Append(Gate.AntennaPower[1]).Append(",").Append(Gate.AntennaPower[2]).Append(",").Append(Gate.AntennaPower[3]).Append("],");
            sb.Append("\"enabled\":[").Append(Gate.AntennaEnabled[0] ? "true" : "false").Append(",").Append(Gate.AntennaEnabled[1] ? "true" : "false").Append(",").Append(Gate.AntennaEnabled[2] ? "true" : "false").Append(",").Append(Gate.AntennaEnabled[3] ? "true" : "false").Append("],");
            sb.Append("\"plugged\":[").Append(Gate.AntennaPlugged[0] ? "true" : "false").Append(",").Append(Gate.AntennaPlugged[1] ? "true" : "false").Append(",").Append(Gate.AntennaPlugged[2] ? "true" : "false").Append(",").Append(Gate.AntennaPlugged[3] ? "true" : "false").Append("],");
            sb.Append("\"returnLoss\":[").Append(Gate.AntennaReturnLossDb[0].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(",").Append(Gate.AntennaReturnLossDb[1].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(",").Append(Gate.AntennaReturnLossDb[2].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(",").Append(Gate.AntennaReturnLossDb[3].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append("],");
            sb.Append("\"detectingAntennas\":").Append(Gate.DetectingAntennas ? "true" : "false").Append(",");
            sb.Append("\"antCounts\":[").Append(Gate.AntennaReadCounts[0]).Append(",").Append(Gate.AntennaReadCounts[1]).Append(",").Append(Gate.AntennaReadCounts[2]).Append(",").Append(Gate.AntennaReadCounts[3]).Append("],");
            sb.Append("\"antRssi\":[").Append(Gate.AntennaLastRssi[0]).Append(",").Append(Gate.AntennaLastRssi[1]).Append(",").Append(Gate.AntennaLastRssi[2]).Append(",").Append(Gate.AntennaLastRssi[3]).Append("],");
            sb.Append("\"gpi1\":").Append(Gate.Gpi1Internal ? "true" : "false").Append(",");
            sb.Append("\"gpi2\":").Append(Gate.Gpi2External ? "true" : "false").Append(",");
            sb.Append("\"gpo1\":").Append(Gate.Gpo1GreenLed ? "true" : "false").Append(",");
            sb.Append("\"gpo2\":").Append(Gate.Gpo2RedLed ? "true" : "false").Append(",");
            sb.Append("\"gpo3\":").Append(Gate.Gpo3Siren ? "true" : "false").Append(",");
            sb.Append("\"gpo4\":").Append(Gate.Gpo4Aux ? "true" : "false").Append(",");
            sb.Append("\"latestTag\":").Append(Gate.LatestTag != null ? SerializeTag(Gate.LatestTag) : "null").Append(",");
            sb.Append("\"tags\":[");
            bool firstG = true;
            foreach (var kv in Gate.GateTags)
            {
                if (!firstG) sb.Append(",");
                sb.Append(SerializeTag(kv.Value));
                firstG = false;
            }
            sb.Append("]");
            sb.Append("}");
            sb.Append("}");
            return sb.ToString();
        }

        private string SerializeUsbManager(UsbGxReaderManager m)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"role\":\"").Append(m.DeviceRole).Append("\",");
            sb.Append("\"connected\":").Append(m.Connected ? "true" : "false").Append(",");
            sb.Append("\"scanning\":").Append(m.Scanning ? "true" : "false").Append(",");
            sb.Append("\"serial\":\"").Append(EscapeJson(m.SerialNumber)).Append("\",");
            sb.Append("\"compileDate\":\"").Append(EscapeJson(m.CompileDate)).Append("\",");
            sb.Append("\"powerDbm\":").Append(m.PowerDbm).Append(",");
            sb.Append("\"maxPowerDbm\":").Append(m.MaxPowerDbm).Append(",");
            sb.Append("\"totalReads\":").Append(m.TotalReads).Append(",");
            sb.Append("\"readsPerSec\":").Append(m.ReadsPerSec).Append(",");
            sb.Append("\"rxPackets\":").Append(m.RxPacketsCount).Append(",");
            sb.Append("\"lastRawRx\":\"").Append(EscapeJson(m.LastRawRx)).Append("\",");
            sb.Append("\"status\":\"").Append(EscapeJson(m.LastStatusMessage)).Append("\",");
            sb.Append("\"latestTag\":").Append(m.LatestTag != null ? SerializeTag(m.LatestTag) : "null").Append(",");
            sb.Append("\"tags\":[");
            bool first = true;
            foreach (var kv in m.Tags)
            {
                if (!first) sb.Append(",");
                sb.Append(SerializeTag(kv.Value));
                first = false;
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string SerializeTag(TagReadRecord t)
        {
            return string.Format(
                "{{\"epcHex\":\"{0}\",\"epcAscii\":\"{1}\",\"tidHex\":\"{2}\",\"chip\":\"{3}\",\"antenna\":{4},\"rssiRaw\":{5},\"rssiDbm\":{6},\"freqMhz\":{7},\"count\":{8},\"lastSeenMs\":{9}}}",
                EscapeJson(t.EpcHex),
                EscapeJson(t.EpcAscii),
                EscapeJson(t.TidHex),
                EscapeJson(t.ChipModel),
                t.Antenna,
                t.RssiRaw,
                t.RssiDbm,
                t.FreqMhz.ToString(System.Globalization.CultureInfo.InvariantCulture),
                t.ReadCount,
                t.LastSeenMs
            );
        }

        private static string ReadBody(HttpListenerRequest req)
        {
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private static void SendJson(HttpListenerResponse res, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            res.ContentType = "application/json; charset=UTF-8";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }

        private static string ExtractJsonStr(string json, string key)
        {
            string pattern = "\"" + key + "\":\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            idx += pattern.Length;
            int end = json.IndexOf("\"", idx);
            if (end < 0) return null;
            return json.Substring(idx, end - idx);
        }

        private static int ExtractJsonInt(string json, string key, int defVal)
        {
            string pattern = "\"" + key + "\":";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return defVal;
            idx += pattern.Length;
            StringBuilder sb = new StringBuilder();
            while (idx < json.Length && (char.IsDigit(json[idx]) || json[idx] == '-'))
            {
                sb.Append(json[idx]);
                idx++;
            }
            int v;
            return int.TryParse(sb.ToString(), out v) ? v : defVal;
        }
    }

    internal static class Program
    {
        private static void StopOtherRunningInstances()
        {
            try
            {
                int currentPid = Process.GetCurrentProcess().Id;
                foreach (Process p in Process.GetProcessesByName("Zyntra RFID Lab"))
                {
                    if (p.Id != currentPid)
                    {
                        try
                        {
                            p.Kill();
                            p.WaitForExit(2000);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        [STAThread]
        private static void Main()
        {
            StopOtherRunningInstances();
            Thread.Sleep(200);

            bool createdNew;
            using (new Mutex(true, "ZyntraRfidLab_SingleInstance", out createdNew))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
