using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NetBar
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 低内存软件渲染模式
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

            base.OnStartup(e);
        }
    }
}
