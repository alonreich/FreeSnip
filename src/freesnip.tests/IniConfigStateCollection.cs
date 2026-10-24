using Xunit;

namespace freesnip.tests
{
    /// <summary>
    /// IniConfig is process-wide static state (directory, config name, save versions). Test classes that
    /// re-initialise it or drive its save queue share this collection so xUnit never runs them in parallel.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class IniConfigStateCollection
    {
        public const string Name = "IniConfigState";
    }
}
