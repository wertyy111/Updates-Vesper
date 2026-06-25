using System;
using Photino.NET;
using System.Reflection;

class Program
{
    static void Main()
    {
        var windowType = typeof(PhotinoWindow);
        foreach (var prop in windowType.GetProperties())
        {
            Console.WriteLine(prop.Name + " " + prop.PropertyType.Name);
        }
    }
}
