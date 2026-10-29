namespace SharpBrier.SampleProject
{
    public abstract class Worker
    {
        public abstract void Routine();

        public void Run()
        {
            Thread thread = new(Routine)
            {
                Name = "Worker thread"
            };
            thread.Start();
        }
    }
}
