namespace SharpBrier.Tests
{
    public abstract class Shape
    {
        public string Name { get; }

        protected Shape(string name)
        {
            Name = name;
        }

        public abstract double Area(double scale);

        public virtual string Describe() => $"{Name}: {Area(1)}";

        public abstract int Count { get; }

        public void NotVirtual() { }

        protected abstract void Hidden(int x, string s);

        public void CallHidden() => Hidden(5, "five");
    }

    internal abstract class InternalBase
    {
        internal abstract string Secret();
    }

    public sealed class SealedClass { }

    public abstract class ObservedInConstructor
    {
        public string SeenInConstructor { get; }

        protected ObservedInConstructor()
        {
            SeenInConstructor = Value();
        }

        public abstract string Value();
    }

    public abstract class WithRefParameter
    {
        public abstract void Mutate(ref int value);
    }

    public abstract class WithGenericMethod
    {
        public abstract T Echo<T>(T value);
    }

    public abstract class Overloaded
    {
        public abstract string Pick(int value);

        public abstract string Pick(string value);
    }
}
