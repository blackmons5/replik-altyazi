using System;
using System.Drawing;

namespace Replik {
    struct SubtitleAnchor {
        internal readonly IntPtr Window;
        internal readonly RectangleF Bounds;
        internal readonly float LineHeight;

        internal SubtitleAnchor(IntPtr window,Rectangle client,Rectangle source,float lineHeight) {
            Window=window;
            Bounds=new RectangleF((source.Left-client.Left)/(float)client.Width,(source.Top-client.Top)/(float)client.Height,
                source.Width/(float)client.Width,source.Height/(float)client.Height);
            LineHeight=lineHeight/client.Height;
        }
    }

    sealed class CaptionSnapshot {
        internal readonly Rectangle Bounds;
        internal readonly string Text;
        internal CaptionSnapshot(Rectangle bounds,string text) {Bounds=bounds; Text=text;}
    }
}
