using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace MyRevitAddin
{
    public class MyApplication : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            string tabName = "Wills Tab";
            try
            {
                // Create the ribbon tab if it does not exist
                try { application.CreateRibbonTab(tabName); } catch { }

                // Create a panel in the custom tab
                RibbonPanel panel = application.CreateRibbonPanel(tabName, "Admin");

                // Add buttons with embedded icons
                panel.AddItem(CreatePushButton("ExecutableCommand", "Execute", "MyRevitAddin.ExecutableCommand", "Resources/Icons/Execute.png"));
                panel.AddItem(CreatePushButton("SuperSearch", "Super Search", "MyRevitAddin.Commands.SuperSearchCommand", "Resources/Icons/Search.png"));

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"OnStartup error: {ex.Message}");
                return Result.Failed;
            }
        }

        private PushButtonData CreatePushButton(string name, string text, string className, string iconResourcePath)
        {
            PushButtonData buttonData = new PushButtonData(name, text, Assembly.GetExecutingAssembly().Location, className);
            buttonData.ToolTip = $"Click to execute {text}";

            // Load the embedded image using a Pack URI
            BitmapImage image = GetEmbeddedImage(iconResourcePath);
            if (image != null)
            {
                buttonData.LargeImage = image;
            }
            else
            {
                TaskDialog.Show("Warning", $"Embedded icon not found: {iconResourcePath}");
            }

            return buttonData;
        }

        private BitmapImage GetEmbeddedImage(string resourcePath)
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream($"MyRevitAddin.{resourcePath.Replace("/", ".")}"))
                {
                    if (stream != null)
                    {
                        BitmapImage bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = stream;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        return bitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to load embedded image: {resourcePath}\n{ex.Message}");
            }
            return null;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
