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
        public const string CurrentVersion = "1.2.0";
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
            if (string.IsNullOrEmpty(hex)) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i + 1 < hex.Length; i += 2)
            {
                int val = Convert.ToInt32(hex.Substring(i, 2), 16);
                if (val == 0) continue;
                if (val >= 32 && val <= 126)
                    sb.Append((char)val);
            }
            return sb.ToString().Trim();
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

                // Initialize after USB plug-in: ensure UsbKbdSwitch=0, Stop, GetReaderInfo, SetPower
                SendFrame(GxProtocol.BuildFrame(0x01, 0x2B, new byte[] { 0x01, 0x01, 0x00 }));
                Thread.Sleep(180);
                SendFrame(GxProtocol.BuildFrame(0x02, 0xFF, null));
                Thread.Sleep(140);
                SendFrame(GxProtocol.BuildFrame(0x01, 0x00, null)); // GetReaderInfo
                Thread.Sleep(140);
                SendFrame(GxProtocol.BuildFrame(0x02, 0x01, new byte[] { 0x01, (byte)PowerDbm })); // SetPower
                Thread.Sleep(140);
                LastStatusMessage = "Conectado por USB HID (Canal 64B Activo)";
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
            SendFrame(GxProtocol.BuildFrame(0x02, 0x10, new byte[] { 0x00, 0x00, 0x00, 0x01, 0x01 }));
            Scanning = true;
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
                        if (!IsDevicePathPresent(DevicePath))
                        {
                            HandlePhysicalDisconnect();
                            return;
                        }
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
            if (nowMs - _lastRpsTickMs >= 1000)
            {
                ReadsPerSec = _readsInCurrentSec;
                _readsInCurrentSec = 0;
                _lastRpsTickMs = nowMs;
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
        public string ReaderIp = "192.168.1.116";
        public int ReaderPort = 9090;
        public bool Connected = false;
        public bool Scanning = false;
        public bool SimulationMode = false;
        public string StatusText = "Desconectado (Conectar BRD-2204 al Router por RJ-45)";

        public int[] AntennaPower = new int[] { 26, 26, 26, 26 }; // ANT 1..4 in dBm (10..33)
        public bool[] AntennaEnabled = new bool[] { true, true, true, true };
        public int[] AntennaReadCounts = new int[] { 0, 0, 0, 0 };
        public int[] AntennaLastRssi = new int[] { -75, -75, -75, -75 };

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
        public ConcurrentDictionary<string, TagReadRecord> GateTags = new ConcurrentDictionary<string, TagReadRecord>();

        public bool ConnectTcp(string ip, int port, out string msg)
        {
            DisconnectTcp();
            ReaderIp = ip;
            ReaderPort = port;
            try
            {
                _tcpClient = new TcpClient();
                var res = _tcpClient.BeginConnect(ip, port, null, null);
                bool success = res.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(1800));
                if (!success || !_tcpClient.Connected)
                {
                    _tcpClient.Close();
                    Connected = false;
                    StatusText = "No se encontró BRD-2204 en " + ip + ":" + port + " (Verifica conexión RJ-45 al Router)";
                    msg = StatusText;
                    return false;
                }
                _tcpClient.EndConnect(res);
                _netStream = _tcpClient.GetStream();
                Connected = true;
                SimulationMode = false;
                _cts = new CancellationTokenSource();
                Task.Run(() => TcpReadLoop(_cts.Token));

                // Stop and query powers
                SendTcpFrame(GxProtocol.BuildFrame(0x02, 0xFF, null));
                Thread.Sleep(60);
                SyncPowerToHardware();
                StatusText = "BRD-2204 Conectado en Red (" + ip + ":" + port + ")";
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

        public void DisconnectTcp()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_netStream != null) _netStream.Close(); } catch { }
            try { if (_tcpClient != null) _tcpClient.Close(); } catch { }
            Connected = false;
            Scanning = false;
        }

        public void SendTcpFrame(byte[] frame)
        {
            if (!Connected || _netStream == null) return;
            try
            {
                _netStream.Write(frame, 0, frame.Length);
                _netStream.Flush();
            }
            catch { }
        }

        public void SyncPowerToHardware()
        {
            byte[] payload = new byte[]
            {
                0x01, (byte)AntennaPower[0],
                0x02, (byte)AntennaPower[1],
                0x03, (byte)AntennaPower[2],
                0x04, (byte)AntennaPower[3]
            };
            SendTcpFrame(GxProtocol.BuildFrame(0x02, 0x01, payload));
        }

        public void SetGpo(int port, bool on)
        {
            if (port == 1) Gpo1GreenLed = on;
            if (port == 2) Gpo2RedLed = on;
            if (port == 3) Gpo3Siren = on;
            if (port == 4) Gpo4Aux = on;

            if (Connected)
            {
                SendTcpFrame(GxProtocol.BuildFrame(0x01, 0x09, new byte[] { (byte)port, (byte)(on ? 1 : 0) }));
            }
        }

        public void RunSmartAutoCalibration(int corridorWidthCm, UsbGxReaderManager usbRef)
        {
            CorridorWidthCm = corridorWidthCm;
            // Calculate optimal dBm based on corridor width and vertical height differences
            // Lower antennas (ANT 2 & ANT 4 at 45cm) need ~1-2 dB less to avoid floor bounce;
            // Upper antennas (ANT 1 & ANT 3 at 115cm) need full penetration for torso/boxes.
            int basePower;
            if (corridorWidthCm <= 120) basePower = 23;
            else if (corridorWidthCm <= 160) basePower = 25;
            else if (corridorWidthCm <= 220) basePower = 28;
            else if (corridorWidthCm <= 300) basePower = 30;
            else basePower = 32;

            // Check if USB reader has nearby background tags to fine-tune threshold
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
            byte[] buf = new byte[512];
            while (!token.IsCancellationRequested && Connected)
            {
                try
                {
                    int n = await _netStream.ReadAsync(buf, 0, buf.Length, token);
                    if (n <= 0) break;
                    // Parse 0x5A frames from TCP stream
                    for (int i = 0; i < n - 8; i++)
                    {
                        if (buf[i] == 0x5A && buf[i + 1] == 0x00 && buf[i + 2] == 0x01)
                        {
                            byte cat = buf[i + 3];
                            byte mid = buf[i + 4];
                            int len = (buf[i + 5] << 8) | buf[i + 6];
                            if (cat == 0x11 && (mid == 0x00 || mid == 0x01) && i + 8 < n)
                            {
                                // LogAppGpiStart / Over
                                byte gpiPort = buf[i + 7];
                                byte level = buf[i + 8];
                                if (gpiPort == 1) Gpi1Internal = (level == 0); // Active low on BRD-2204
                                if (gpiPort == 2) Gpi2External = (level == 0);
                            }
                            break;
                        }
                    }
                }
                catch
                {
                    break;
                }
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

            string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zyntra.ico");
            if (File.Exists(icoPath))
            {
                Icon = new Icon(icoPath);
            }

            _trayIcon = new NotifyIcon
            {
                Icon = this.Icon ?? SystemIcons.Application,
                Text = "Zyntra RFID Lab — Activo junto al reloj (Puerto " + _port + ")",
                Visible = true
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("🌐 Abrir Consola Web de Antenas", null, (s, e) => OpenBrowser());
            menu.Items.Add("🖥️ Mostrar Ventana de Estado", null, (s, e) => RestoreFromTray());
            menu.Items.Add("🔄 Forzar Reconexión USB", null, (s, e) => Task.Run(() => ForceReconnectUsbDevices()));
            menu.Items.Add("-");
            menu.Items.Add("❌ Salir Completamente", null, (s, e) => ExitApp());
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (s, e) => RestoreFromTray();

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
                Task.Run(() => AutoDetectUsbDevices());
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
                while (_running)
                {
                    try
                    {
                        Thread.Sleep(1500);
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
                            if (!Dc06.Connected && paths.Count >= 1)
                            {
                                AutoDetectUsbDevices();
                            }
                            else if (!Dc01.Connected && paths.Count >= 2)
                            {
                                AutoDetectUsbDevices();
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
                if (paths.Count >= 1)
                {
                    if (!Dc06.Connected || !UsbGxReaderManager.IsDevicePathPresent(Dc06.DevicePath))
                    {
                        if (Dc06.PowerDbm <= 0) Dc06.PowerDbm = 27;
                        if (Dc06.Connect(paths[0]))
                        {
                            Dc06.StartInventory();
                        }
                    }
                }
                if (paths.Count >= 2)
                {
                    if (!Dc01.Connected || !UsbGxReaderManager.IsDevicePathPresent(Dc01.DevicePath))
                    {
                        Dc01.Connect(paths[1]);
                    }
                }
                UpdateWindowLabel();
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
                "• BRD-DC06 (USB): {0} ({1})\n• BRD-DC01 (USB): {2}\n• Consola Web: http://localhost:{3}",
                Dc06.Connected ? "CONECTADA" : "No detectada",
                Dc06.Scanning ? "Leyendo en vivo a " + Dc06.PowerDbm + " dBm" : "En espera",
                Dc01.Connected ? "CONECTADA" : "Esperando conexión USB",
                _port
            );
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
                    if (path == "/api/gate/connect" && method == "POST")
                    {
                        string ip = ExtractJsonStr(body, "ip") ?? "192.168.1.116";
                        int port = ExtractJsonInt(body, "port", 9090);
                        string msg;
                        bool ok = Gate.ConnectTcp(ip, port, out msg);
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
                        if (ant >= 1 && ant <= 4)
                        {
                            Gate.AntennaEnabled[ant - 1] = !Gate.AntennaEnabled[ant - 1];
                        }
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
            sb.Append("\"connected\":").Append(Gate.Connected ? "true" : "false").Append(",");
            sb.Append("\"scanning\":").Append(Gate.Scanning ? "true" : "false").Append(",");
            sb.Append("\"status\":\"").Append(EscapeJson(Gate.StatusText)).Append("\",");
            sb.Append("\"rssiThreshold\":").Append(Gate.RssiThresholdDbm).Append(",");
            sb.Append("\"corridorWidthCm\":").Append(Gate.CorridorWidthCm).Append(",");
            sb.Append("\"calibrationReport\":\"").Append(EscapeJson(Gate.LastCalibrationReport)).Append("\",");
            sb.Append("\"powers\":[").Append(Gate.AntennaPower[0]).Append(",").Append(Gate.AntennaPower[1]).Append(",").Append(Gate.AntennaPower[2]).Append(",").Append(Gate.AntennaPower[3]).Append("],");
            sb.Append("\"enabled\":[").Append(Gate.AntennaEnabled[0] ? "true" : "false").Append(",").Append(Gate.AntennaEnabled[1] ? "true" : "false").Append(",").Append(Gate.AntennaEnabled[2] ? "true" : "false").Append(",").Append(Gate.AntennaEnabled[3] ? "true" : "false").Append("],");
            sb.Append("\"gpi1\":").Append(Gate.Gpi1Internal ? "true" : "false").Append(",");
            sb.Append("\"gpi2\":").Append(Gate.Gpi2External ? "true" : "false").Append(",");
            sb.Append("\"gpo1\":").Append(Gate.Gpo1GreenLed ? "true" : "false").Append(",");
            sb.Append("\"gpo2\":").Append(Gate.Gpo2RedLed ? "true" : "false").Append(",");
            sb.Append("\"gpo3\":").Append(Gate.Gpo3Siren ? "true" : "false").Append(",");
            sb.Append("\"gpo4\":").Append(Gate.Gpo4Aux ? "true" : "false");
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
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (new Mutex(true, "ZyntraRfidLab_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    try { Process.Start("http://localhost:8090"); } catch { }
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
