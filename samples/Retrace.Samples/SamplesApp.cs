using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;

[assembly: ExtensionApplication(typeof(Retrace.Samples.SamplesApp))]

namespace Retrace.Samples
{
    /// <summary>Loads with AutoCAD and adds the Samples ribbon tab as soon as the ribbon exists.</summary>
    public class SamplesApp : IExtensionApplication
    {
        public void Initialize()
        {
            if (ComponentManager.Ribbon != null)
            {
                SamplesTab.Create();
                return;
            }
            ComponentManager.ItemInitialized += OnItemInitialized;
        }

        public void Terminate()
        {
            ComponentManager.ItemInitialized -= OnItemInitialized;
        }

        private static void OnItemInitialized(object? sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon == null) return;
            ComponentManager.ItemInitialized -= OnItemInitialized;
            SamplesTab.Create();
        }
    }
}
