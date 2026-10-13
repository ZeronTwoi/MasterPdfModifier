using System.Runtime.CompilerServices;
using PdfModifier.Core;

namespace PdfModifier.Tests;

internal static class TestSetup
{
    /// <summary>
    /// The test host is a background process, which Windows slows down to save power: recognition tests then take
    /// several times as long, and the more of them run, the longer each takes. The app switches this off for itself
    /// at start-up; so do the tests.
    /// </summary>
    [ModuleInitializer]
    public static void Initialize() => Performance.RunAtFullSpeed();
}
