using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// Keeps the editor's main loop turning for a while, from any thread. An unfocused Unity editor (beside the code editor,
    /// the usual setup) barely runs EditorApplication.update, where hot reload picks up changes and applies compiled ones:
    /// a save took ~25 s to show. The file watcher sees it at once on its own thread, so it wakes the main thread instead —
    /// on macOS by posting an application-defined NSEvent (postEvent:atStart: is thread safe). Running a selector on the
    /// main thread (performSelectorOnMainThread) was tried first and doesn't make the editor tick. Elsewhere it does nothing.
    /// </summary>
    internal static class EditorWakeUp
    {
        private const int IntervalMilliseconds = 100;

        private static readonly object TimerLock = new object();
        private static Timer _timer;
        private static long _awakeUntilTicks;

        /// <summary>Wakes the main loop every 100 ms for the next <paramref name="milliseconds"/>.</summary>
        public static void KeepAwakeFor(int milliseconds)
        {
#if UNITY_EDITOR_OSX
            Interlocked.Exchange(ref _awakeUntilTicks, DateTime.UtcNow.AddMilliseconds(milliseconds).Ticks);
            lock (TimerLock)
            {
                if (_timer == null)
                {
                    _timer = new Timer(Tick, null, 0, IntervalMilliseconds);
                }
                else
                {
                    _timer.Change(0, IntervalMilliseconds);
                }
            }
#endif
        }

#if UNITY_EDITOR_OSX
        private const string ObjC = "/usr/lib/libobjc.dylib";

        [DllImport(ObjC)]
        private static extern IntPtr objc_getClass(string name);

        [DllImport(ObjC)]
        private static extern IntPtr sel_registerName(string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector);

        [StructLayout(LayoutKind.Sequential)]
        private struct NSPoint
        {
            public double X, Y;
        }

        // +[NSEvent otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:]
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendOtherEvent(IntPtr receiver, IntPtr selector, ulong type, NSPoint location, ulong modifierFlags,
            double timestamp, long windowNumber, IntPtr context, short subtype, long data1, long data2);

        // -[NSApplication postEvent:atStart:]
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendPostEvent(IntPtr receiver, IntPtr selector, IntPtr theEvent, bool atStart);

        private const ulong NSEventTypeApplicationDefined = 15;

        private static IntPtr _application, _eventClass, _otherEvent, _postEvent;

        private static void Tick(object state)
        {
            if (DateTime.UtcNow.Ticks > Interlocked.Read(ref _awakeUntilTicks))
            {
                lock (TimerLock)
                {
                    _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                }
                return;
            }

            try
            {
                if (_application == IntPtr.Zero)
                {
                    _application = SendMessage(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                    _eventClass = objc_getClass("NSEvent");
                    _otherEvent = sel_registerName("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:");
                    _postEvent = sel_registerName("postEvent:atStart:");
                }

                // an application-defined event: nothing handles it, but the editor's loop turns for it as for input
                var theEvent = SendOtherEvent(_eventClass, _otherEvent, NSEventTypeApplicationDefined, default, 0, 0, 0, IntPtr.Zero, 0, 0, 0);
                if (theEvent != IntPtr.Zero)
                {
                    SendPostEvent(_application, _postEvent, theEvent, false);
                }
            }
            catch (Exception)
            {
                // a wake-up is an optimisation: the editor still ticks on its own, only slower
                lock (TimerLock)
                {
                    _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }
        }
#endif
    }
}
