using ListeningSubtitles.Core;
using ListeningSubtitles.UI;
using System.Security.Principal;

namespace ListeningSubtitles;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var preview = args.Length == 2 && args[0] == "--preview";
        if (preview) Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        if (args.Length != 0 && !preview) return;
        using var mutex = new Mutex(true, "Local\\ListeningSubtitles-" + WindowsIdentity.GetCurrent().User?.Value + (preview ? "-preview" : ""), out var first);
        if (!first)
        {
            if (!preview) MessageBox.Show("听力字幕已经在运行。请先在现有窗口保留或导出记录，再退出旧窗口并打开此版本。", "已保留正在运行的字幕窗口");
            return;
        }
        try
        {
            var store = new ListeningSettingsStore(preview ? Path.GetFullPath(args[1]) : null);
            using var form = new SubtitleForm(store, store.Load());
            if (preview)
            {
                form.Shown += (_, _) =>
                {
                    form.Preview(
                        new("demo-1", 1, 1, DateTimeOffset.Now, "You do not have to understand every word to follow the story.", true, "你不必听懂每一个词，也能跟上故事的内容。", "completed"),
                        new("demo-2", 1, 2, DateTimeOffset.Now, "Try listening for the main idea first.", false));
                    Directory.CreateDirectory(args[1]);
                    using var picture = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(picture, new Rectangle(Point.Empty, form.Size));
                    picture.Save(Path.Combine(args[1], "subtitle-window.png"));
                    form.Close();
                };
            }
            Application.Run(form);
        }
        catch (Exception ex)
        {
            if (preview) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "preview-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
            else MessageBox.Show(ex.Message, "听力字幕无法启动");
        }
    }
}
