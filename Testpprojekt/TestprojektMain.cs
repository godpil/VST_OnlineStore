using Tools.Console;
using System.Reflection;

namespace Testpprojekt
{
    internal class TestprojektMain
    {

        public static int Main(string[] args)
        {
            ConsoleTools.tl("Programmstart ( " + DateTime.Now.ToString("HH:mm:ss") + ")" + Assembly.GetExecutingAssembly().GetName().FullName + "...");

           

            ConsoleTools.tl("...Programmende!");
            return 0;
        }
    }
}
