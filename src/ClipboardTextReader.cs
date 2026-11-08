using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace CoursePilot
{
    public interface ITextReadDesktop
    {
        Task<string> ReadFocusedText(CancellationToken cancellation);
    }

    internal interface IClipboardReadPort
    {
        object Backup();
        void Restore(object backup);
        void SetMarker(string marker);
        uint Sequence { get; }
        string ReadText();
        void CheckTarget();
        void SelectAndCopy(CancellationToken token);
        void Wait(int milliseconds, CancellationToken token);
    }

    internal static class ClipboardTextReader
    {
        internal static Task<string> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            IntPtr target = Native.GetForegroundWindow();
            TaskCompletionSource<string> result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread worker = new Thread(delegate()
            {
                try { result.TrySetResult(Read(new WindowsClipboardReadPort(target), token)); }
                catch (OperationCanceledException) { result.TrySetCanceled(); }
                catch (QuizPauseException e) { result.TrySetException(e); }
                catch (Exception) { result.TrySetException(new QuizPauseException("输入框实际文本回读失败，已暂停。")); }
            });
            worker.IsBackground = true; worker.SetApartmentState(ApartmentState.STA); worker.Start();
            return result.Task;
        }

        // Isolated from Windows so copy failure, cancellation and restoration can be tested without touching the user's clipboard.
        internal static string Read(IClipboardReadPort port, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); port.CheckTarget();
            object backup = port.Backup();
            try
            {
                token.ThrowIfCancellationRequested(); port.CheckTarget();
                string marker = "CoursePilot-readback-" + Guid.NewGuid().ToString("N");
                port.SetMarker(marker); uint sequence = port.Sequence;
                token.ThrowIfCancellationRequested(); port.CheckTarget(); port.SelectAndCopy(token);
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    token.ThrowIfCancellationRequested(); port.CheckTarget();
                    if (port.Sequence != sequence)
                    {
                        string value = port.ReadText();
                        token.ThrowIfCancellationRequested(); port.CheckTarget();
                        if (value != null && value != marker) return value;
                    }
                    port.Wait(50, token);
                }
                throw new QuizPauseException("未能复制到输入框实际文本，已暂停，未采用剪贴板旧内容。");
            }
            finally
            {
                try { port.Restore(backup); }
                catch (Exception) { throw new QuizPauseException("原剪贴板未能恢复，已暂停，请检查剪贴板状态。"); }
                finally { IDisposable disposable = backup as IDisposable; if (disposable != null) disposable.Dispose(); }
            }
        }
    }

    internal sealed class WindowsClipboardReadPort : IClipboardReadPort
    {
        private readonly IntPtr target;
        internal WindowsClipboardReadPort(IntPtr target) { this.target = target; }
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        private sealed class Snapshot : IDisposable
        {
            internal readonly Forms.DataObject Data = new Forms.DataObject();
            internal readonly List<IDisposable> Owned = new List<IDisposable>();
            public void Dispose() { foreach (IDisposable value in Owned) value.Dispose(); }
        }
        public object Backup()
        {
            Forms.IDataObject original = Forms.Clipboard.GetDataObject();
            if (original == null || original.GetFormats(false).Length == 0) return null;
            Snapshot snapshot = new Snapshot();
            try
            {
                foreach (string format in original.GetFormats(false))
                {
                    object value = original.GetData(format, false);
                    if (value == null || Marshal.IsComObject(value)) throw new InvalidOperationException("Clipboard format cannot be preserved");
                    Stream stream = value as Stream; Image image = value as Image;
                    if (stream != null)
                    {
                        if (!stream.CanSeek) throw new InvalidOperationException("Clipboard stream cannot be preserved");
                        long position = stream.Position; MemoryStream copy = new MemoryStream(); snapshot.Owned.Add(copy);
                        try { stream.Position = 0; stream.CopyTo(copy); copy.Position = 0; } finally { stream.Position = position; }
                        value = copy;
                    }
                    else if (image != null) { Image copy = (Image)image.Clone(); snapshot.Owned.Add(copy); value = copy; }
                    else if (!value.GetType().IsSerializable) throw new InvalidOperationException("Clipboard format cannot be preserved");
                    snapshot.Data.SetData(format, false, value);
                }
                return snapshot;
            }
            catch { snapshot.Dispose(); throw new QuizPauseException("原剪贴板内容无法完整备份，已暂停，剪贴板未改动。"); }
        }
        public void Restore(object backup)
        {
            Snapshot snapshot = backup as Snapshot;
            if (snapshot == null) Forms.Clipboard.Clear();
            else Forms.Clipboard.SetDataObject(snapshot.Data, true, 5, 100);
        }
        public void SetMarker(string marker) { Forms.Clipboard.SetDataObject(marker, true, 5, 100); }
        public uint Sequence { get { return GetClipboardSequenceNumber(); } }
        public string ReadText()
        { return Forms.Clipboard.ContainsText(Forms.TextDataFormat.UnicodeText) ? Forms.Clipboard.GetText(Forms.TextDataFormat.UnicodeText) : null; }
        public void CheckTarget()
        { if (target == IntPtr.Zero || Native.GetForegroundWindow() != target) throw new QuizPauseException("实际文本回读期间目标窗口变化，已暂停。"); }
        private static void Shortcut(ushort key)
        {
            Native.SendKey(0x11, false);
            try { Native.SendKey(key, false); }
            finally { try { Native.SendKey(key, true); } finally { Native.SendKey(0x11, true); } }
        }
        public void SelectAndCopy(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); CheckTarget(); Shortcut(0x41);
            Wait(50, token); CheckTarget(); Shortcut(0x43);
        }
        public void Wait(int milliseconds, CancellationToken token)
        { if (token.WaitHandle.WaitOne(milliseconds)) token.ThrowIfCancellationRequested(); }
    }
}
