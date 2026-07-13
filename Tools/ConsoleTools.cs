namespace Tools
{
    namespace Console
    {
        public class ConsoleTools
        {
            public static void tl(string message)
            {
                System.Console.WriteLine(message);
            }
            public static void t(string message)
            {
                System.Console.Write(message);
            }

            public static void tl(object value)
            {
                throw new NotImplementedException();
            }
        }
    }
    namespace Program {
        public class ProgramTools {
            public static void StartProgram(string [] args) {
                if (args != null && args.Length > 0) {
                    Console.ConsoleTools.tl($"Programm startet mit Parameter(n):");
                    foreach (var arg in args) {
                        Console.ConsoleTools.tl($"\t -  {arg}");
                    }
                } 
                else {
                    Console.ConsoleTools.tl($"\nProgramm startet ohne Parameter....\n");
                }
            }
            public static int EndProgram(string arg) {
                Console.ConsoleTools.tl($"\nProgrammende erreicht.\t (" + arg + ")\n(Taste drücken, zum Beenden...)");
                System.Console.ReadKey();
                return 0;
            }
        }
    }
}
