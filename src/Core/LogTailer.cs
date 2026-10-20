using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CrashCatcher.Core
{
    public enum TailState { Starting, Missing, Watching, Reset, Error }

    /// <summary>
    /// Дочитывает новые строки из лога, который пишет другой процесс.
    /// Понимает пересоздание файла (Unity переписывает Player.log при каждом запуске),
    /// обрезание и временное отсутствие файла.
    /// </summary>
    public sealed class LogTailer : IDisposable
    {
        public string FilePath { get; private set; }
        public int PollMs { get; set; }
        public bool ReadExisting { get; private set; }

        /// <summary>Новая строка (вызывается из рабочего потока).</summary>
        public event Action<string> Line;
        /// <summary>Смена состояния (вызывается из рабочего потока).</summary>
        public event Action<TailState, string> StateChanged;
        /// <summary>Вызывается после каждого опроса (для таймаутов парсера).</summary>
        public event Action Polled;

        public TailState State { get; private set; }

        Thread _thread;
        volatile bool _stop;
        bool _initialized, _wasMissing;
        long _pos;
        string _fileId;
        Decoder _decoder;
        readonly StringBuilder _partial = new StringBuilder();
        DateTime _partialSince;
        readonly byte[] _buf = new byte[64 * 1024];
        readonly char[] _chars = new char[64 * 1024 + 8];
        string _lastError;

        public LogTailer(string path, bool readExisting, int pollMs)
        {
            FilePath = path;
            ReadExisting = readExisting;
            PollMs = pollMs;
            State = TailState.Starting;
            _decoder = new UTF8Encoding(false).GetDecoder();
        }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "LogTailer" };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            var t = _thread;
            _thread = null;
            if (t != null && t != Thread.CurrentThread) t.Join(2000);
        }

        public void Dispose() { Stop(); }

        void Loop()
        {
            while (!_stop)
            {
                try { PollOnce(); }
                catch (Exception ex) { SetState(TailState.Error, ex.Message); }
                try { var p = Polled; if (p != null) p(); } catch { }
                for (int waited = 0; waited < PollMs && !_stop; waited += 50) Thread.Sleep(Math.Min(50, PollMs));
            }
        }

        void SetState(TailState s, string info)
        {
            if (s == State && s != TailState.Reset && (s != TailState.Error || info == _lastError)) return;
            State = s == TailState.Reset ? TailState.Watching : s;
            if (s == TailState.Error) _lastError = info; else _lastError = null;
            var h = StateChanged;
            if (h != null) h(s, info);
        }

        /// <summary>Один опрос файла. Публичный — для тестов.</summary>
        public void PollOnce()
        {
            if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            {
                if (_initialized) _wasMissing = true;
                SetState(TailState.Missing, FilePath);
                return;
            }

            FileStream fs;
            try
            {
                fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            }
            catch (FileNotFoundException) { if (_initialized) _wasMissing = true; SetState(TailState.Missing, FilePath); return; }
            catch (DirectoryNotFoundException) { if (_initialized) _wasMissing = true; SetState(TailState.Missing, FilePath); return; }
            catch (IOException ex) { SetState(TailState.Error, ex.Message); return; }
            catch (UnauthorizedAccessException ex) { SetState(TailState.Error, ex.Message); return; }

            using (fs)
            {
                string id = FileIdentity.Get(fs);
                long len = fs.Length;

                if (!_initialized)
                {
                    _initialized = true;
                    _fileId = id;
                    // Если файла не было при старте и он появился — это новый лог, читаем с начала.
                    bool appearedLater = State == TailState.Missing;
                    _pos = (ReadExisting || appearedLater) ? 0 : len;
                    SetState(TailState.Watching, FilePath);
                }
                else if (_wasMissing || (id != null && _fileId != null && id != _fileId) || len < _pos)
                {
                    _wasMissing = false;
                    _fileId = id;
                    _pos = 0;
                    _decoder = new UTF8Encoding(false).GetDecoder();
                    _partial.Length = 0;
                    SetState(TailState.Reset, FilePath);
                }
                else if (State != TailState.Watching)
                {
                    SetState(TailState.Watching, FilePath);
                }

                if (len > _pos)
                {
                    fs.Seek(_pos, SeekOrigin.Begin);
                    while (_pos < len && !_stop)
                    {
                        int want = (int)Math.Min(_buf.Length, len - _pos);
                        int n = fs.Read(_buf, 0, want);
                        if (n <= 0) break;
                        _pos += n;
                        int c = _decoder.GetChars(_buf, 0, n, _chars, 0, false);
                        Consume(_chars, c);
                    }
                }
                else if (_partial.Length > 0 && (DateTime.UtcNow - _partialSince).TotalMilliseconds > 1000)
                {
                    // Хвост без перевода строки (например, последняя строка перед крашем).
                    var tail = _partial.ToString().TrimEnd('\r');
                    _partial.Length = 0;
                    Emit(tail);
                }
            }
        }

        void Consume(char[] chars, int count)
        {
            int start = 0;
            for (int i = 0; i < count; i++)
            {
                if (chars[i] != '\n') continue;
                string line;
                if (_partial.Length > 0)
                {
                    _partial.Append(chars, start, i - start);
                    line = _partial.ToString();
                    _partial.Length = 0;
                }
                else line = new string(chars, start, i - start);
                if (line.Length > 0 && line[line.Length - 1] == '\r') line = line.Substring(0, line.Length - 1);
                Emit(line);
                start = i + 1;
            }
            if (start < count)
            {
                if (_partial.Length == 0) _partialSince = DateTime.UtcNow;
                _partial.Append(chars, start, count - start);
                if (_partial.Length > 1000000) { Emit(_partial.ToString()); _partial.Length = 0; }
            }
        }

        void Emit(string line)
        {
            if (line.Length > 4000) line = line.Substring(0, 4000) + " …";
            var h = Line;
            if (h != null) h(line);
        }
    }

    /// <summary>Идентификатор файла (том + индекс). Позволяет заметить, что файл с тем же именем — уже другой.</summary>
    public static class FileIdentity
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION info);

        static readonly bool IsWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

        public static string Get(FileStream fs)
        {
            if (!IsWindows) return null;
            try
            {
                BY_HANDLE_FILE_INFORMATION info;
                if (!GetFileInformationByHandle(fs.SafeFileHandle, out info)) return null;
                return info.VolumeSerialNumber.ToString("X8") + ":" + info.FileIndexHigh.ToString("X8") + info.FileIndexLow.ToString("X8");
            }
            catch { return null; }
        }
    }
}
