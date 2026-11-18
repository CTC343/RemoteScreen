using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteScreen
{
    internal static class Desktop
    {
        private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders()
            .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
        [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon,
            int width, int height, uint step, IntPtr brush, uint flags);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorInfo
        {
            internal int Size, Flags;
            internal IntPtr Cursor;
            internal Point Position;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            [MarshalAs(UnmanagedType.Bool)] internal bool IsIcon;
            internal int HotspotX, HotspotY;
            internal IntPtr Mask, Color;
        }

        internal static void UsePhysicalPixels()
        {
            SetProcessDPIAware();
        }

        internal static byte[] Capture()
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            double scale = Math.Min(1, Math.Min(1024.0 / bounds.Width, 768.0 / bounds.Height));
            int width = Math.Max(1, (int)(bounds.Width * scale));
            int height = Math.Max(1, (int)(bounds.Height * scale));
            using (var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb))
            using (var output = new MemoryStream())
            using (var settings = new EncoderParameters(1))
            {
                using (Graphics canvas = Graphics.FromImage(full))
                {
                    canvas.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                    PaintCursor(canvas, bounds);
                }

                settings.Param[0] = new EncoderParameter(Encoder.Quality, 46L);
                if (scale == 1)
                    full.Save(output, Jpeg, settings);
                else
                {
                    using (var reduced = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                    {
                        using (Graphics canvas = Graphics.FromImage(reduced))
                        {
                            canvas.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            canvas.DrawImage(full, new Rectangle(0, 0, width, height));
                        }
                        reduced.Save(output, Jpeg, settings);
                    }
                }
                int size = checked((int)output.Length);
                var frame = new byte[size + 1];
                frame[0] = Wire.Frame;
                Buffer.BlockCopy(output.GetBuffer(), 0, frame, 1, size);
                return frame;
            }
        }

        private static void PaintCursor(Graphics canvas, Rectangle bounds)
        {
            var cursor = new CursorInfo { Size = Marshal.SizeOf(typeof(CursorInfo)) };
            IconInfo icon;
            if (!GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0 ||
                !bounds.Contains(cursor.Position) || !GetIconInfo(cursor.Cursor, out icon))
                return;
            try
            {
                IntPtr dc = canvas.GetHdc();
                try
                {
                    DrawIconEx(dc, cursor.Position.X - bounds.Left - icon.HotspotX,
                        cursor.Position.Y - bounds.Top - icon.HotspotY, cursor.Cursor, 0, 0, 0, IntPtr.Zero, 3);
                }
                finally { canvas.ReleaseHdc(dc); }
            }
            finally
            {
                if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
                if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
            }
        }
    }

    internal sealed class RemoteInput : IDisposable
    {
        private static readonly int InputSize = Marshal.SizeOf(typeof(Input));
        private static readonly uint[] Down = { 0x0002, 0x0008, 0x0020 };
        private static readonly uint[] Up = { 0x0004, 0x0010, 0x0040 };
        private readonly bool[] buttons = new bool[3];
        private readonly HashSet<int> keys = new HashSet<int>();

        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            internal uint Kind;
            internal Payload Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct Payload
        {
            [FieldOffset(0)] internal MouseInput Mouse;
            [FieldOffset(0)] internal KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            internal int X, Y;
            internal uint Value, Flags, Time;
            internal UIntPtr Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            internal ushort Code, Scan;
            internal uint Flags, Time;
            internal UIntPtr Extra;
        }

        internal void Apply(byte[] message)
        {
            byte type = message[0];
            switch (type)
            {
                case Wire.Move:
                    if (message.Length == 5) MoveTo(message);
                    break;
                case Wire.Button:
                    if (message.Length == 7 && message[5] < buttons.Length && message[6] <= 1)
                    {
                        MoveTo(message);
                        int index = message[5];
                        bool pressed = message[6] == 1;
                        if (Mouse(pressed ? Down[index] : Up[index], 0))
                            buttons[index] = pressed;
                    }
                    break;
                case Wire.Wheel:
                    if (message.Length == 7)
                    {
                        MoveTo(message);
                        Mouse(0x0800, unchecked((uint)(short)Wire.ReadWord(message, 5)));
                    }
                    break;
                case Wire.Key:
                    if (message.Length == 4 && message[1] > 0 && message[1] < 255 &&
                        message[2] <= 1 && message[3] <= 1)
                    {
                        int key = message[1] | (message[2] << 8);
                        bool pressed = message[3] == 1;
                        if (Keyboard(key, pressed))
                        {
                            if (pressed) keys.Add(key);
                            else keys.Remove(key);
                        }
                    }
                    break;
                case Wire.Release:
                    if (message.Length == 1) ReleaseAll();
                    break;
            }
        }

        private static void MoveTo(byte[] message)
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            int x = bounds.Left + (int)(((long)Wire.ReadWord(message, 1) * (bounds.Width - 1) + 32767) / 65535);
            int y = bounds.Top + (int)(((long)Wire.ReadWord(message, 3) * (bounds.Height - 1) + 32767) / 65535);
            SetCursorPos(x, y);
        }

        private static bool Mouse(uint flags, uint value)
        {
            var input = new Input { Data = new Payload { Mouse = new MouseInput { Flags = flags, Value = value } } };
            return SendInput(1, new[] { input }, InputSize) == 1;
        }

        private static bool Keyboard(int key, bool pressed)
        {
            uint flags = (uint)((key >> 8) | (pressed ? 0 : 2));
            var input = new Input
            {
                Kind = 1,
                Data = new Payload { Keyboard = new KeyboardInput { Code = (ushort)(key & 255), Flags = flags } }
            };
            return SendInput(1, new[] { input }, InputSize) == 1;
        }

        public void Dispose()
        {
            ReleaseAll();
        }

        private void ReleaseAll()
        {
            foreach (int key in keys) Keyboard(key, false);
            keys.Clear();
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i]) Mouse(Up[i], 0);
                buttons[i] = false;
            }
        }
    }
}
