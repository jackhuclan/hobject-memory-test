namespace HObjectMemory.Lifetime.Tests;

[TestClass]
public sealed class Rectangle1TestDataTests
{
    [TestMethod]
    public void Constructor_AndProperties_ReturnConfiguredValues()
    {
        Rectangle1Case testCase = new("Case", 1.5, 2.5, 10.5, 20.5);

        Assert.AreEqual("Case", testCase.Name);
        Assert.AreEqual(1.5, testCase.Row1);
        Assert.AreEqual(2.5, testCase.Column1);
        Assert.AreEqual(10.5, testCase.Row2);
        Assert.AreEqual(20.5, testCase.Column2);
        Assert.AreEqual("Case", testCase.ToString());
    }

    [TestMethod]
    public void WidthAndHeight_FollowInclusivePixelCount()
    {
        Rectangle1Case normal = new("Normal", 0, 0, 9, 19);
        Rectangle1Case singlePoint = new("Single", 10, 10, 10, 10);

        Assert.AreEqual(20, normal.Width);
        Assert.AreEqual(10, normal.Height);
        Assert.AreEqual(1, singlePoint.Width);
        Assert.AreEqual(1, singlePoint.Height);
    }

    [TestMethod]
    public void Cases_CoverExpectedCountAndUniqueNames()
    {
        Assert.AreEqual(7, Rectangle1TestData.Cases.Count);
        CollectionAssert.AreEqual(
            new[]
            {
                "Normal_256x256", "Small_1x1", "Wide_512x8", "Tall_8x512",
                "Large_1024x1024", "Line_ZeroWidth", "NegativeOrigin"
            },
            Rectangle1TestData.Cases.Select(x => x.Name).ToArray());
    }

    [TestMethod]
    public void EveryCase_SatisfiesHalconCoordinateConstraint()
    {
        foreach (Rectangle1Case testCase in Rectangle1TestData.Cases)
        {
            Assert.IsTrue(testCase.Row2 >= testCase.Row1, testCase.Name);
            Assert.IsTrue(testCase.Column2 >= testCase.Column1, testCase.Name);
            Assert.AreEqual(testCase.Column2 - testCase.Column1 + 1, testCase.Width, testCase.Name);
            Assert.AreEqual(testCase.Row2 - testCase.Row1 + 1, testCase.Height, testCase.Name);
        }
    }

    [TestMethod]
    public void Cases_IncludeDegenerateAndNegativeOriginBoundaries()
    {
        Rectangle1Case singlePoint = Rectangle1TestData.Cases.Single(x => x.Name == "Small_1x1");
        Rectangle1Case zeroWidth = Rectangle1TestData.Cases.Single(x => x.Name == "Line_ZeroWidth");
        Rectangle1Case negative = Rectangle1TestData.Cases.Single(x => x.Name == "NegativeOrigin");

        Assert.AreEqual(1, singlePoint.Width);
        Assert.AreEqual(1, singlePoint.Height);
        Assert.AreEqual(1, zeroWidth.Width);
        Assert.IsTrue(negative.Row1 < 0 && negative.Column1 < 0);
    }
}
