namespace SharpBrier.Tests
{
    public class AnonymTests
    {
        private static Shape BuildCircle(string name = "circle") =>
            Anonym<Shape>.Build([name], b => b
                .HasReturn<double>(nameof(Shape.Area), [typeof(double)], (_, a) => Math.PI * (double)a[0])
                .HasReturn<int>("get_Count", [], (_, _) => 42)
                .NoReturn("Hidden", [typeof(int), typeof(string)], (_, _) => { }));

        [Fact]
        public void PassesConstructorArgumentsToBaseConstructor()
        {
            Shape shape = BuildCircle("circle");

            Assert.Equal("circle", shape.Name);
        }

        [Fact]
        public void HasReturnOverridesMethodWithValueTypeResultAndParameters()
        {
            Shape shape = BuildCircle();

            Assert.Equal(2 * Math.PI, shape.Area(2), precision: 9);
        }

        [Fact]
        public void BaseVirtualMethodDispatchesToOverride()
        {
            Shape shape = BuildCircle();

            Assert.Equal($"circle: {Math.PI}", shape.Describe());
        }

        [Fact]
        public void OverridesAbstractPropertyThroughGetterName()
        {
            Shape shape = BuildCircle();

            Assert.Equal(42, shape.Count);
        }

        [Fact]
        public void OverridesProtectedMethodAndReceivesThisAndArguments()
        {
            Shape? receivedThis = null;
            object[]? receivedArguments = null;
            Shape shape = Anonym<Shape>.Build(["p"], b => b
                .HasReturn<double>(nameof(Shape.Area), [typeof(double)], (_, _) => 0)
                .HasReturn<int>("get_Count", [], (_, _) => 0)
                .NoReturn("Hidden", [typeof(int), typeof(string)], (@this, a) =>
                {
                    receivedThis = @this;
                    receivedArguments = a;
                }));

            shape.CallHidden();

            Assert.Same(shape, receivedThis);
            Assert.Equal([5, "five"], receivedArguments);
        }

        [Fact]
        public void InstancesFromSeparateBuildsKeepTheirOwnBodies()
        {
            Shape circle = BuildCircle();
            Shape square = Anonym<Shape>.Build(["square"], b => b
                .HasReturn<double>(nameof(Shape.Area), [typeof(double)], (_, a) => 4 * (double)a[0]));

            Assert.Equal(Math.PI, circle.Area(1));
            Assert.Equal(4, square.Area(1));
        }

        [Fact]
        public void HasReturnOfObjectIsUnboxedToDeclaredReturnType()
        {
            Shape shape = Anonym<Shape>.Build(["o"], b => b
                .HasReturn<object>(nameof(Shape.Area), [typeof(double)], (_, _) => 1.5));

            Assert.Equal(1.5, shape.Area(0));
        }

        [Fact]
        public void OverridesNonAbstractVirtualMethod()
        {
            Shape shape = Anonym<Shape>.Build(["d"], b => b
                .HasReturn<double>(nameof(Shape.Area), [typeof(double)], (_, _) => 0)
                .HasReturn<string>(nameof(Shape.Describe), [], (@this, _) => "custom " + @this.Name));

            Assert.Equal("custom d", shape.Describe());
        }

        [Fact]
        public void ResolvesOverloadsByParameterTypes()
        {
            Overloaded instance = Anonym<Overloaded>.Build([], b => b
                .HasReturn<string>(nameof(Overloaded.Pick), [typeof(int)], (_, _) => "int")
                .HasReturn<string>(nameof(Overloaded.Pick), [typeof(string)], (_, _) => "string"));

            Assert.Equal("int", instance.Pick(1));
            Assert.Equal("string", instance.Pick("x"));
        }

        [Fact]
        public void MissingAbstractMembersThrowNotImplementedWhenCalled()
        {
            Shape shape = Anonym<Shape>.Build(["partial"], b => b
                .HasReturn<double>(nameof(Shape.Area), [typeof(double)], (_, _) => 0));

            NotImplementedException exception = Assert.Throws<NotImplementedException>(() => shape.Count);
            Assert.Contains("get_Count", exception.Message);
        }

        [Fact]
        public void SubclassesInternalTypeWithoutInternalsVisibleTo()
        {
            InternalBase instance = Anonym<InternalBase>.Build([], b => b
                .HasReturn<string>(nameof(InternalBase.Secret), [], (_, _) => "shh"));

            Assert.Equal("shh", instance.Secret());
        }

        [Fact]
        public void OverrideIsVisibleToVirtualCallsFromBaseConstructor()
        {
            ObservedInConstructor instance = Anonym<ObservedInConstructor>.Build([], b => b
                .HasReturn<string>(nameof(ObservedInConstructor.Value), [], (_, _) => "override"));

            Assert.Equal("override", instance.SeenInConstructor);
        }

        [Fact]
        public void SameShapeReusesGeneratedTypeWhileInstancesKeepTheirOwnDelegates()
        {
            Shape first = Anonym<Shape>.Build(["first"], b => b
                .HasReturn<double>(nameof(Shape.Area), [typeof(double)], (_, _) => 1));
            Shape second = Anonym<Shape>.Build(["second"], b => b
                .HasReturn<double>(nameof(Shape.Area), [typeof(double)], (_, _) => 2));

            Assert.Same(first.GetType(), second.GetType());
            Assert.Equal(1, first.Area(0));
            Assert.Equal(2, second.Area(0));
        }

        [Fact]
        public void InstancesBuiltInLoopCaptureTheirOwnClosureValues()
        {
            List<Shape> shapes = new();
            for (int i = 0; i < 3; i++)
            {
                int captured = i;
                shapes.Add(Anonym<Shape>.Build([$"shape{i}"], b => b
                    .HasReturn<double>(nameof(Shape.Area), [typeof(double)], (_, _) => captured)));
            }

            Assert.Single(shapes.Select(s => s.GetType()).Distinct());
            Assert.Equal([0, 1, 2], shapes.Select(s => s.Area(0)));
        }

        [Fact]
        public void DifferentShapesGetDifferentGeneratedTypes()
        {
            Shape areaOnly = Anonym<Shape>.Build(["a"], b => b
                .HasReturn<double>(nameof(Shape.Area), [typeof(double)], (_, _) => 0));
            Shape areaAndCount = Anonym<Shape>.Build(["a"], b => b
                .HasReturn<double>(nameof(Shape.Area), [typeof(double)], (_, _) => 0)
                .HasReturn<int>("get_Count", [], (_, _) => 0));
            Shape objectReturn = Anonym<Shape>.Build(["a"], b => b
                .HasReturn<object>(nameof(Shape.Area), [typeof(double)], (_, _) => 0.0));

            Assert.NotSame(areaOnly.GetType(), areaAndCount.GetType());
            Assert.NotSame(areaOnly.GetType(), objectReturn.GetType());
        }

        [Fact]
        public void BranchingInsideBuildActionAtOneCallSiteIsSafe()
        {
            Shape[] shapes = new Shape[2];
            for (int i = 0; i < 2; i++)
            {
                bool withCount = i == 1;
                shapes[i] = Anonym<Shape>.Build(["b"], b =>
                {
                    b.HasReturn<double>(nameof(Shape.Area), [typeof(double)], (_, _) => 0);
                    if (withCount)
                        b.HasReturn<int>("get_Count", [], (_, _) => 7);
                });
            }

            Assert.NotSame(shapes[0].GetType(), shapes[1].GetType());
            Assert.Throws<NotImplementedException>(() => shapes[0].Count);
            Assert.Equal(7, shapes[1].Count);
        }

        [Fact]
        public void GeneratedTypeNameMentionsCallSite()
        {
            Shape shape = Anonym<Shape>.Build(["n"], b => b
                .HasReturn<double>(nameof(Shape.Area), [typeof(double)], (_, _) => 0)
                .HasReturn<int>("get_Count", [], (_, _) => 1)
                .NoReturn("Hidden", [typeof(int), typeof(string)], (_, _) => { }));

            Assert.Matches(@"^Shape<Anonym>AnonymTests:\d+#\d+$", shape.GetType().Name);
        }

        [Fact]
        public void RejectsSealedClass()
        {
            Assert.Throws<InvalidOperationException>(() => Anonym<SealedClass>.Build([], _ => { }));
        }

        [Fact]
        public void RejectsNonVirtualMethod()
        {
            Assert.Throws<InvalidOperationException>(() =>
                Anonym<Shape>.Build(["x"], b => b.NoReturn(nameof(Shape.NotVirtual), [], (_, _) => { })));
        }

        [Fact]
        public void RejectsUnknownMethod()
        {
            Assert.Throws<MissingMethodException>(() =>
                Anonym<Shape>.Build(["x"], b => b.NoReturn("Nope", [], (_, _) => { })));
        }

        [Fact]
        public void RejectsConstructorArgumentsThatMatchNoConstructor()
        {
            Assert.Throws<MissingMethodException>(() => Anonym<Shape>.Build([1], _ => { }));
        }

        [Fact]
        public void RejectsNoReturnForMethodWithResult()
        {
            Assert.Throws<InvalidOperationException>(() =>
                Anonym<Shape>.Build(["x"], b => b.NoReturn(nameof(Shape.Area), [typeof(double)], (_, _) => { })));
        }

        [Fact]
        public void RejectsHasReturnForVoidMethod()
        {
            Assert.Throws<InvalidOperationException>(() =>
                Anonym<Shape>.Build(["x"], b => b.HasReturn<int>("Hidden", [typeof(int), typeof(string)], (_, _) => 0)));
        }

        [Fact]
        public void RejectsIncompatibleReturnType()
        {
            Assert.Throws<InvalidOperationException>(() =>
                Anonym<Shape>.Build(["x"], b => b.HasReturn<string>(nameof(Shape.Area), [typeof(double)], (_, _) => "")));
        }

        [Fact]
        public void RejectsDuplicateOverride()
        {
            Assert.Throws<InvalidOperationException>(() => Anonym<Shape>.Build(["x"], b => b
                .HasReturn<double>(nameof(Shape.Area), [typeof(double)], (_, _) => 0)
                .HasReturn<double>(nameof(Shape.Area), [typeof(double)], (_, _) => 1)));
        }

        [Fact]
        public void RejectsRefParameters()
        {
            Assert.Throws<NotSupportedException>(() => Anonym<WithRefParameter>.Build([], b => b
                .NoReturn(nameof(WithRefParameter.Mutate), [typeof(int).MakeByRefType()], (_, _) => { })));
        }

        [Fact]
        public void RejectsGenericMethods()
        {
            Assert.Throws<NotSupportedException>(() => Anonym<WithGenericMethod>.Build([], b => b
                .HasReturn<object>(nameof(WithGenericMethod.Echo), [typeof(object)], (_, a) => a[0])));
        }

        [Fact]
        public void RejectsInterfaces()
        {
            Assert.Throws<NotSupportedException>(() => Anonym<IDisposable>.Build([], _ => { }));
        }
    }
}
