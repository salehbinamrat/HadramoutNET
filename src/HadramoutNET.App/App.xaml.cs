using System.Globalization;
using System.Windows;
using HadramoutNET.Core.Visual;

namespace HadramoutNET.App;

public partial class App : Application
{
    public App()
    {
        // Applies to new async/dispatcher threads as well as the UI thread.
        // Do not change CurrentUICulture: Arabic labels/resources remain Arabic.
        CultureInfo english = EnglishDigitPresentation.Culture;
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.CurrentCulture = english;
    }
}
