using System;
using System.Threading.Tasks.Sources;
class Test {
    static void Main() {
        var core = new ManualResetValueTaskSourceCore<bool>();
        Console.WriteLine($"Version before: {core.Version}");
        core.Reset();
        Console.WriteLine($"Version after Reset: {core.Version}");
        core.Reset();
        Console.WriteLine($"Version after second Reset: {core.Version}");
    }
}
