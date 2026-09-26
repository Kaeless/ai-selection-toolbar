using System.Drawing;

namespace AiSelectionToolbar.Selection
{
    public sealed class SelectionSnapshot
    {
        public SelectionSnapshot(string text, string sourceApplication, string sourceTitle, Rectangle bounds)
        {
            Text = text;
            SourceApplication = sourceApplication;
            SourceTitle = sourceTitle;
            Bounds = bounds;
        }

        public string Text { get; private set; }
        public string SourceApplication { get; private set; }
        public string SourceTitle { get; private set; }
        public Rectangle Bounds { get; private set; }
    }
}
