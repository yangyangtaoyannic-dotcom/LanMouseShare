using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Agent
{
    /// <summary>One remote file exposed through the clipboard (bytes fetched at paste time).</summary>
    public sealed class VirtualFileEntry
    {
        public string FileName;
        public long Length;
        public long LastWriteUtcTicks;
        public Func<string> FetchLocalPath;
    }

    /// <summary>
    /// 剪贴板数据对象：把对端复制的文件以延迟渲染的方式发布到本机剪贴板
    /// （CFSTR_FILEDESCRIPTORW + CFSTR_FILECONTENTS）。文件名复制后立即可见
    /// （Explorer 右键"粘贴"不再灰），字节只在应用真正读取文件（即粘贴时）
    /// 才向对端拉取。
    ///
    /// 0.2.0-beta-fix5 加固：
    /// - 对象创建与 OleSetClipboard 放到进程级长驻 STA 线程上执行：OleSetClipboard
    ///   要求线程经 OleInitialize 初始化（OLE 剪贴板状态挂在 STA 上，MTA 线程即使
    ///   CoInitializeEx 成功，OleSetClipboard 仍返回 CO_E_NOTINITIALIZED 0x800401F0，
    ///   fix4 双机日志实证）。该 STA 线程带消息泵，Explorer 等粘贴方读剪贴板时
    ///   GetData 回调被 COM 封送回该线程并由泵分发——拉取大文件只阻塞这条 STA
    ///   线程，UI 与输入钩子不受影响（若在 UI 线程渲染会导致整个 Agent 假死）。
    /// - FileContents 以 TYMED_ISTREAM 惰性流发布（枚举只列 ISTREAM）：本机
    ///   随后新复制时 OleFlushClipboard 只拿到廉价的流指针，不会把全部文件
    ///   强制渲染出来（否则每次普通复制都会触发一次完整远端拉取）；Win7
    ///   等要求 HGLOBAL 的调用方在粘贴时才现取字节。
    /// - OleSetClipboard 对 CLIPBRD_E_CANT_OPEN 重试，解决剪贴板被 Explorer
    ///   等进程瞬时占用导致的发布失败。
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class VirtualFileClipboard : System.Runtime.InteropServices.ComTypes.IDataObject
    {
        private const short TymedHg = 1;
        private const short TymedIStream = 4;
        private const int ClrbrdECantOpen = unchecked((int)0x800401D0);
        private const int GmemMoveable = 0x0002;
        private const int FdFilesize = 0x00000040;
        private const int FdAttributes = 0x00000004;
        private const int FdWritestime = 0x00000020;
        private const int FileAttributeNormal = 0x00000080;

        private static readonly int DescriptorFormat = RegisterClipboardFormat("FileGroupDescriptorW");
        private static readonly int ContentsFormat = RegisterClipboardFormat("FileContents");
        private static readonly int DropEffectFormat = RegisterClipboardFormat("Preferred DropEffect");

        private static VirtualFileClipboard _current;

        /// <summary>HRESULT from the last OleSetClipboard call (0 = success).</summary>
        public static int LastHResult;

        private readonly VirtualFileEntry[] _files;
        private readonly Dictionary<int, string> _cachedPaths = new Dictionary<int, string>();
        private readonly object _gate = new object();

        private VirtualFileClipboard(VirtualFileEntry[] files)
        {
            _files = files;
        }

        // ------------------------------------------------------------------
        // STA 发布通道：OleSetClipboard 要求调用线程经 OleInitialize 初始化
        // （OLE 剪贴板状态挂在 STA 上；MTA 线程即使 CoInitializeEx 成功，
        // OleSetClipboard 仍返回 CO_E_NOTINITIALIZED 0x800401F0，fix4 实测）。
        // 对象创建在进程级长驻 STA 线程上，该线程带消息泵：Explorer 等粘贴
        // 方读剪贴板时，GetData 回调被 COM 封送回本线程的消息队列由泵分发，
        // 拉取大文件只阻塞这条 STA 线程，UI 与输入钩子不受影响。线程一旦
        // 退出，STA 被拆除，剪贴板上的对象随之失效，所以该线程进程级长驻。
        // ------------------------------------------------------------------
        private static readonly BlockingCollection<Action> StaQueue = new BlockingCollection<Action>();
        private static Thread _staThread;
        private static readonly object StaGate = new object();

        private static void EnsureStaThread()
        {
            lock (StaGate)
            {
                if (_staThread != null && _staThread.IsAlive)
                {
                    return;
                }

                var thread = new Thread(StaMain);
                thread.Name = "VirtualClipboardSTA";
                thread.IsBackground = true;
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                _staThread = thread;
            }
        }

        private static void StaMain()
        {
            int oleHr = OleInitialize(IntPtr.Zero);
            AppLog.Debug("clipboard.files.virtual.ole", "sta.hr=0x" + oleHr.ToString("X8"));

            while (true)
            {
                // 1) 处理发布/清除动作（对象必须在本 STA 线程上创建）。
                Action action;
                while (StaQueue.TryTake(out action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("clipboard.files.virtual.worker " + ex.Message);
                    }
                }

                // 2) 泵消息：COM 把粘贴方进程的 GetData 回调封送到本线程，
                // 消息泵负责分发；没有泵，跨进程粘贴会永远卡住。
                MSG msg;
                while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PmRemove))
                {
                    if (msg.message == WmQuit)
                    {
                        return;
                    }

                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                // 3) 空转时短暂休眠，避免占满一个核。
                Thread.Sleep(5);
            }
        }

        public static bool Publish(VirtualFileEntry[] files)
        {
            if (files == null || files.Length == 0)
            {
                return false;
            }

            try
            {
                EnsureStaThread();
                bool ok = false;
                Exception error = null;
                var done = new ManualResetEventSlim(false);
                StaQueue.Add(delegate
                {
                    try
                    {
                        // 对象在 STA 线程上创建：OleSetClipboard 在此成功；
                        // 后续 GetData 回调经消息泵封送回本线程（fix5）。
                        var data = new VirtualFileClipboard(files);
                        int hr = 0;
                        for (int attempt = 0; attempt < 25; attempt++)
                        {
                            hr = OleSetClipboard(data);
                            if (hr == 0 || hr != ClrbrdECantOpen)
                            {
                                break;
                            }

                            // 剪贴板被别的进程（Explorer/复制对话框）短暂占用。
                            Thread.Sleep(50);
                        }

                        LastHResult = hr;
                        if (hr != 0)
                        {
                            AppLog.Warn("clipboard.files.virtual.failed hr=0x" + hr.ToString("X8"));
                            return;
                        }

                        _current = data;
                        ok = true;
                        AppLog.Event("INFO", "clipboard.files.virtual",
                            "count=" + files.Length + " first=" + files[0].FileName);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        done.Set();
                    }
                });

                if (!done.Wait(4000))
                {
                    AppLog.Warn("clipboard.files.virtual.publish.timeout");
                    return false;
                }

                if (!ok)
                {
                    if (error != null)
                    {
                        AppLog.Warn("clipboard.files.virtual.failed " + error.Message);
                    }

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("clipboard.files.virtual.failed " + ex.Message);
                return false;
            }
        }

        public static void Clear()
        {
            // OleSetClipboard(null) 同样要求在 STA 线程调用，走发布通道执行。
            try
            {
                EnsureStaThread();
                var done = new ManualResetEventSlim(false);
                StaQueue.Add(delegate
                {
                    try
                    {
                        OleSetClipboard(null);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        done.Set();
                    }
                });
                done.Wait(2000);
            }
            catch
            {
            }

            _current = null;
        }

        private static int NormalizeCf(short cfFormat)
        {
            // FORMATETC.cfFormat 是有符号 short；注册格式 ID 落在 0xC000-0xFFFF，
            // 以 short 形式传回时为负数，必须归一到 RegisterClipboardFormat
            // 返回的正 int 再比较（fix6：格式 ID 符号错位修复）。
            return cfFormat < 0 ? cfFormat + 65536 : cfFormat;
        }

        void System.Runtime.InteropServices.ComTypes.IDataObject.GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = new STGMEDIUM();
            int cf = NormalizeCf(format.cfFormat);
            AppLog.Debug("clipboard.files.probe",
                "cf=" + cf + " lindex=" + format.lindex + " tymed=" + (int)format.tymed);

            if (cf == DescriptorFormat)
            {
                medium.tymed = (TYMED)TymedHg;
                medium.unionmember = AllocHGlobal(BuildFileDescriptors());
                return;
            }

            if (cf == ContentsFormat)
            {
                // OLE flush / 批量渲染用 lindex=-1 请求 FileContents：此时不拉取，
                // 返回 DV_E_FORMATETC 让系统跳过（防止发布时被强制渲染整批文件，
                // 即"复制即传输"回归）。真实粘贴按文件 lindex=0..n-1 请求。
                if (format.lindex < 0)
                {
                    throw new COMException("Per-file index required.", unchecked((int)0x80040064));
                }

                int index = format.lindex;
                if (index >= _files.Length)
                {
                    throw new COMException("File index out of range.", unchecked((int)0x80040064));
                }

                // 枚举里宣告的是 ISTREAM：给粘贴进程一个惰性流，Read 时才真正
                // 拉取远端文件（粘贴时才传输）。传统调用方（Win7 Explorer 等）
                // 请求 HGLOBAL 时走下面的现取分支。
                if ((format.tymed & (TYMED)TymedIStream) != 0)
                {
                    var wrapper = new StreamWrapper(new LazyFileStream(this, index), delegate
                    {
                        return new LazyFileStream(this, index);
                    });
                    medium.tymed = (TYMED)TymedIStream;
                    medium.unionmember = Marshal.GetComInterfaceForObject(wrapper, typeof(IStream));
                    return;
                }

                string path = ResolveLocalPath(index);
                if (path == null)
                {
                    throw new COMException("Remote file fetch failed.", unchecked((int)0x80040064));
                }

                var content = File.ReadAllBytes(path);
                medium.tymed = (TYMED)TymedHg;
                medium.unionmember = AllocHGlobal(content);
                AppLog.Debug("clipboard.files.served",
                    "index=" + index + " bytes=" + content.Length + " name=" + _files[index].FileName);
                return;
            }

            if (cf == DropEffectFormat)
            {
                medium.tymed = (TYMED)TymedHg;
                medium.unionmember = AllocHGlobal(new byte[] { 1, 0, 0, 0 });
                return;
            }

            throw new COMException("Unsupported clipboard format " + cf, unchecked((int)0x80040064));
        }

        private string ResolveLocalPath(int index)
        {
            // GetData 与 ISTREAM 惰性读都在发布 STA 线程上经消息泵串行分发，
            // 但 HGLOBAL 分支可能被 COM 封送到别的线程；锁住整段拉取逻辑
            // 防止同一文件被并发双拉。
            lock (_gate)
            {
                string cached;
                if (_cachedPaths.TryGetValue(index, out cached) && File.Exists(cached))
                {
                    return cached;
                }

                var entry = _files[index];
                AppLog.Debug("clipboard.files.fetch", "index=" + index + " name=" + entry.FileName);
                var path = entry.FetchLocalPath != null ? entry.FetchLocalPath() : null;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    _cachedPaths[index] = path;
                    return path;
                }

                AppLog.Warn("clipboard.files.fetch.failed index=" + index + " name=" + entry.FileName);
                return null;
            }
        }

        private byte[] BuildFileDescriptors()
        {
            const int descriptorSize = 596;
            var buffer = new byte[descriptorSize * _files.Length];
            for (int i = 0; i < _files.Length; i++)
            {
                var entry = _files[i];
                int offset = i * descriptorSize;
                WriteInt(buffer, offset + 0, FdFilesize | FdAttributes | FdWritestime);
                WriteInt(buffer, offset + 36, FileAttributeNormal);
                long ticks = entry.LastWriteUtcTicks > 0 ? entry.LastWriteUtcTicks : DateTime.UtcNow.Ticks;
                WriteTicks(buffer, offset + 44, ticks);
                WriteTicks(buffer, offset + 52, ticks);
                WriteTicks(buffer, offset + 60, ticks);
                WriteInt(buffer, offset + 68, (int)(entry.Length >> 32));
                WriteInt(buffer, offset + 72, (int)(entry.Length & 0xFFFFFFFF));
                string name = entry.FileName ?? "file";
                if (name.Length > 259)
                {
                    name = name.Substring(0, 259);
                }

                var nameBytes = Encoding.Unicode.GetBytes(name);
                Array.Copy(nameBytes, 0, buffer, offset + 76, Math.Min(nameBytes.Length, 520));
            }

            return buffer;
        }

        void System.Runtime.InteropServices.ComTypes.IDataObject.GetDataHere(ref FORMATETC format, ref STGMEDIUM medium)
        {
            // Explorer 标准读 FileContents 走 GetDataHere（调用方提供存储）。
            int cf = NormalizeCf(format.cfFormat);
            if (cf != ContentsFormat)
            {
                throw new COMException("GetDataHere is not supported.", unchecked((int)0x80040064));
            }

            if (format.lindex < 0 || format.lindex >= _files.Length)
            {
                throw new COMException("File index out of range.", unchecked((int)0x80040064));
            }

            if ((format.tymed & (TYMED)TymedIStream) != 0 && medium.unionmember != IntPtr.Zero)
            {
                // 调用方提供目标 IStream：拉取后写入其流。
                var target = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                string path = ResolveLocalPath(format.lindex);
                if (path == null)
                {
                    throw new COMException("Remote file fetch failed.", unchecked((int)0x80040064));
                }

                using (var fs = File.OpenRead(path))
                {
                    var buffer = new byte[65536];
                    int read;
                    while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        var chunk = new byte[read];
                        Array.Copy(buffer, chunk, read);
                        target.Write(chunk, read, IntPtr.Zero);
                    }
                }
                return;
            }

            if ((format.tymed & (TYMED)TymedHg) != 0)
            {
                // 调用方提供 HGLOBAL：拉取后拷入（按调用方缓冲大小截断不了，
                // 这里 GlobalReAlloc 成实际大小再写，Win7 Explorer 即此路径）。
                string path = ResolveLocalPath(format.lindex);
                if (path == null)
                {
                    throw new COMException("Remote file fetch failed.", unchecked((int)0x80040064));
                }

                var content = File.ReadAllBytes(path);
                IntPtr h = medium.unionmember;
                IntPtr resized = GlobalReAlloc(h, (IntPtr)content.Length, 0x0002 /*GMEM_MOVEABLE*/);
                if (resized == IntPtr.Zero)
                {
                    throw new COMException("GlobalReAlloc failed.", unchecked((int)0x8007000E));
                }

                medium.unionmember = resized;
                IntPtr dest = GlobalLock(resized);
                try
                {
                    Marshal.Copy(content, 0, dest, content.Length);
                }
                finally
                {
                    GlobalUnlock(resized);
                }

                AppLog.Debug("clipboard.files.served",
                    "getdatahere index=" + format.lindex + " bytes=" + content.Length + " name=" + _files[format.lindex].FileName);
                return;
            }

            throw new COMException("Unsupported tymed.", unchecked((int)0x80040069)); // DV_E_TYMED
        }

        int System.Runtime.InteropServices.ComTypes.IDataObject.QueryGetData(ref FORMATETC format)
        {
            int cf = NormalizeCf(format.cfFormat);
            if (cf == DescriptorFormat || cf == DropEffectFormat)
            {
                return 0;
            }

            if (cf == ContentsFormat)
            {
                if (format.lindex < 0 || format.lindex >= _files.Length)
                {
                    return unchecked((int)0x80040064);
                }

                // 接受 HGLOBAL（Win7 等传统调用方）与 ISTREAM（惰性流）两种介质。
                if ((format.tymed & (TYMED)TymedHg) != 0 || (format.tymed & (TYMED)TymedIStream) != 0)
                {
                    return 0;
                }

                return unchecked((int)0x80040069); // DV_E_TYMED
            }

            return unchecked((int)0x80040064);
        }

        int System.Runtime.InteropServices.ComTypes.IDataObject.GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
        {
            formatOut = formatIn;
            formatOut.ptd = IntPtr.Zero;
            return 0x00040003;
        }

        void System.Runtime.InteropServices.ComTypes.IDataObject.SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
        {
            throw new COMException("SetData is not supported.", unchecked((int)0x80040064));
        }

        IEnumFORMATETC System.Runtime.InteropServices.ComTypes.IDataObject.EnumFormatEtc(DATADIR direction)
        {
            var formats = new List<FORMATETC>();
            formats.Add(MakeFormat(DescriptorFormat, -1, (TYMED)TymedHg));
            // Contents 只枚举 ISTREAM：OleFlushClipboard 依据枚举逐项渲染旧
            // 剪贴板，ISTREAM 让 flush 拿到廉价流指针而不触发实际文件拉取；
            // 要求 HGLOBAL 的调用方由 QueryGetData/GetData 在粘贴时现取。
            for (int i = 0; i < _files.Length; i++)
            {
                formats.Add(MakeFormat(ContentsFormat, i, (TYMED)TymedIStream));
            }

            formats.Add(MakeFormat(DropEffectFormat, -1, (TYMED)TymedHg));
            return new FormatEnumerator(formats.ToArray());
        }

        private static FORMATETC MakeFormat(int cfFormat, int lindex, TYMED tymed)
        {
            var format = new FORMATETC();
            format.cfFormat = (short)cfFormat;
            format.ptd = IntPtr.Zero;
            format.dwAspect = DVASPECT.DVASPECT_CONTENT;
            format.lindex = lindex;
            format.tymed = tymed;
            return format;
        }

        int System.Runtime.InteropServices.ComTypes.IDataObject.DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink pAdvSink, out int connection)
        {
            connection = 0;
            return unchecked((int)0x80040003);
        }

        void System.Runtime.InteropServices.ComTypes.IDataObject.DUnadvise(int connection)
        {
        }

        int System.Runtime.InteropServices.ComTypes.IDataObject.EnumDAdvise(out IEnumSTATDATA newEnum)
        {
            newEnum = null;
            return unchecked((int)0x80040003);
        }

        [ComVisible(true)]
        private sealed class FormatEnumerator : IEnumFORMATETC
        {
            private readonly FORMATETC[] _formats;
            private int _position;

            public FormatEnumerator(FORMATETC[] formats)
            {
                _formats = formats;
            }

            void IEnumFORMATETC.Clone(out IEnumFORMATETC newEnum)
            {
                var clone = new FormatEnumerator(_formats);
                clone._position = _position;
                newEnum = clone;
            }

            int IEnumFORMATETC.Next(int celt, FORMATETC[] rgelt, int[] pceltFetched)
            {
                int copied = 0;
                while (copied < celt && _position < _formats.Length)
                {
                    rgelt[copied] = _formats[_position];
                    copied++;
                    _position++;
                }

                if (pceltFetched != null && pceltFetched.Length > 0)
                {
                    pceltFetched[0] = copied;
                }

                return copied == celt ? 0 : 1;
            }

            int IEnumFORMATETC.Reset()
            {
                _position = 0;
                return 0;
            }

            int IEnumFORMATETC.Skip(int celt)
            {
                _position += celt;
                return _position <= _formats.Length ? 0 : 1;
            }
        }

        /// <summary>
        /// 惰性文件流：首次真正读取（Read/Seek 触发 Length 等）时才调用
        /// FetchLocalPath 拉取远端文件并打开，做到"粘贴时才传输"。
        /// </summary>
        private sealed class LazyFileStream : Stream
        {
            private readonly VirtualFileClipboard _owner;
            private readonly int _index;
            private Stream _inner;
            private bool _failed;

            public LazyFileStream(VirtualFileClipboard owner, int index)
            {
                _owner = owner;
                _index = index;
            }

            private Stream Inner
            {
                get
                {
                    if (_inner == null && !_failed)
                    {
                        string path = _owner.ResolveLocalPath(_index);
                        if (path == null)
                        {
                            _failed = true;
                            throw new FileNotFoundException("远程文件拉取失败：" + _owner._files[_index].FileName);
                        }

                        _inner = File.OpenRead(path);
                    }

                    return _inner;
                }
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return true; } }
            public override bool CanWrite { get { return false; } }

            public override long Length { get { return Inner.Length; } }

            public override long Position
            {
                get { return Inner.Position; }
                set { Inner.Position = value; }
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return Inner.Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                return Inner.Seek(offset, origin);
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && _inner != null)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        /// <summary>把托管 Stream 包装成 COM IStream（FileContents 的 TYMED_ISTREAM 载体）。</summary>
        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class StreamWrapper : IStream
        {
            private readonly Stream _stream;
            private readonly Func<Stream> _cloneFactory;

            public StreamWrapper(Stream stream, Func<Stream> cloneFactory)
            {
                _stream = stream;
                _cloneFactory = cloneFactory;
            }

            void IStream.Read(byte[] pv, int cb, IntPtr pcbRead)
            {
                int read = _stream.Read(pv, 0, cb);
                if (pcbRead != IntPtr.Zero)
                {
                    Marshal.WriteInt32(pcbRead, read);
                }
            }

            void IStream.Write(byte[] pv, int cb, IntPtr pcbWritten)
            {
                throw new NotSupportedException("流只读。");
            }

            void IStream.Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
            {
                long pos = _stream.Seek(dlibMove, (SeekOrigin)dwOrigin);
                if (plibNewPosition != IntPtr.Zero)
                {
                    Marshal.WriteInt64(plibNewPosition, pos);
                }
            }

            void IStream.SetSize(long libNewSize)
            {
                throw new NotSupportedException("流只读。");
            }

            void IStream.CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
            {
                var buffer = new byte[65536];
                long totalRead = 0;
                long totalWritten = 0;
                while (cb < 0 || totalRead < cb)
                {
                    int want = cb < 0 ? buffer.Length : (int)Math.Min(buffer.Length, cb - totalRead);
                    int read = _stream.Read(buffer, 0, want);
                    if (read <= 0)
                    {
                        break;
                    }

                    pstm.Write(buffer, read, IntPtr.Zero);
                    totalRead += read;
                    totalWritten += read;
                }

                if (pcbRead != IntPtr.Zero)
                {
                    Marshal.WriteInt64(pcbRead, totalRead);
                }

                if (pcbWritten != IntPtr.Zero)
                {
                    Marshal.WriteInt64(pcbWritten, totalWritten);
                }
            }

            void IStream.Commit(int grfCommitFlags)
            {
                _stream.Flush();
            }

            void IStream.Revert()
            {
            }

            void IStream.LockRegion(long libOffset, long cb, int dwLockType)
            {
            }

            void IStream.UnlockRegion(long libOffset, long cb, int dwLockType)
            {
            }

            void IStream.Stat(out System.Runtime.InteropServices.ComTypes.STATSTG pstatstg, int grfStatFlag)
            {
                pstatstg = new System.Runtime.InteropServices.ComTypes.STATSTG();
                pstatstg.type = 2; // STGTY_STREAM
                pstatstg.cbSize = _stream.Length;
            }

            void IStream.Clone(out IStream ppstm)
            {
                // Clone 时重新开一条惰性流（从零开始读），而不是复制当前游标。
                ppstm = new StreamWrapper(_cloneFactory(), _cloneFactory);
            }
        }

        private static void WriteInt(byte[] buffer, int offset, int value)
        {
            var bytes = BitConverter.GetBytes(value);
            Array.Copy(bytes, 0, buffer, offset, 4);
        }

        private static void WriteTicks(byte[] buffer, int offset, long utcTicks)
        {
            long fileTime = utcTicks > 0 ? utcTicks : DateTime.UtcNow.Ticks;
            var bytes = BitConverter.GetBytes(fileTime);
            Array.Copy(bytes, 0, buffer, offset, 8);
        }

        private static IntPtr AllocHGlobal(byte[] data)
        {
            // fix6：GMEM_FIXED——GlobalAlloc 返回的 HGLOBAL 直接就是可写指针。
            // 旧代码用 GMEM_MOVEABLE 却未 GlobalLock 就 Marshal.Copy，直接写坏
            // 全局堆导致 Agent 进程崩溃（Explorer 读取剪贴板描述符时触发）。
            IntPtr pointer = GlobalAlloc(0x0000 /*GMEM_FIXED*/, (UIntPtr)(uint)Math.Max(data.Length, 1));
            if (pointer == IntPtr.Zero)
            {
                throw new OutOfMemoryException("clipboard global alloc failed.");
            }

            Marshal.Copy(data, 0, pointer, data.Length);
            return pointer;
        }

        [DllImport("ole32.dll")]
        private static extern int OleInitialize(IntPtr pvReserved);

        [DllImport("ole32.dll")]
        private static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject dataObject);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterClipboardFormat(string format);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(int flags, UIntPtr bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalReAlloc(IntPtr handle, IntPtr bytes, int flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

        private const int PmRemove = 1;
        private const int WmQuit = 0x12;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam;
            public IntPtr lParam;
            public int time;
            public int ptX;
            public int ptY;
        }
    }
}
