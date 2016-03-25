using NUnit.Framework;

namespace Es.Tcg.Test
{

    [TestFixture]
    public class TcgTf
    {
        [Test]
        public void Test()
        {
            Assert.AreEqual("p4f2",Program.BranchPrefix("p4f2.foo"));
            Assert.AreEqual("p4", Program.BranchParentPrefix("p4f2.foo"));
            Assert.AreEqual("master", Program.BranchParentPrefix("p4.foo"));
            Assert.AreEqual("master", Program.BranchPrefix("master"));
        }
    }
}
