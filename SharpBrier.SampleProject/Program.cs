namespace SharpBrier.SampleProject
{
    internal static class Program
    {

        static void Main(string[] args)
        {
            Worker myWorker = Anonym<Worker>.Build([], (builder) =>
            {
                builder.NoReturn(nameof(Worker.Routine), [], (@this, _) =>
                {
                    Console.WriteLine("Hello from thread " + Thread.CurrentThread.Name);
                });
            });
            myWorker.Run();
        }
    }
}
