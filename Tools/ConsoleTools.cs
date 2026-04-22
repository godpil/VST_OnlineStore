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
}
