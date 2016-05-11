namespace AsyncToolkit
{
    internal static class Extensions
    {
        public static T Cast<T>(this object o)
        {
            return (T)o;
        }

        public static T As<T>(this object o)
            where T : class
        {
            return o as T;
        }
    }
}