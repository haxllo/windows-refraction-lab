using RefractionLab.Logic;
using Xunit;

namespace RefractionLab.Tests;

public class BezelMapTests
{
    private const int W = 200, H = 120;
    private const float R = 12, B = 32;

    private static (float X, float Y) D(int x, int y) => BezelMap.Displacement(x, y, W, H, R, B);

    [Fact]
    public void InteriorIsNeutral()
    {
        Assert.Equal((0f, 0f), D(W / 2, H / 2));
        Assert.Equal((0f, 0f), D(60, 60));
        Assert.Equal((0f, 0f), D(W - 60, H - 60));
    }

    [Fact]
    public void EdgesSampleTowardTheCenter()
    {
        Assert.True(D(0, H / 2).X > 0.9f && D(0, H / 2).Y == 0);          // left edge reads from the right
        Assert.True(D(W - 1, H / 2).X < -0.9f);                          // right edge reads from the left
        Assert.True(D(W / 2, 0).Y > 0.9f && D(W / 2, 0).X == 0);          // top edge reads from below
        Assert.True(D(W / 2, H - 1).Y < -0.9f);                          // bottom edge reads from above
    }

    [Fact]
    public void StrengthFallsOffMonotonicallyInward()
    {
        float previous = float.MaxValue;
        for (int x = 0; x < (int)B; x++)
        {
            float mag = MathF.Abs(D(x, H / 2).X);
            Assert.True(mag <= previous + 1e-6f, $"x={x}");
            previous = mag;
        }
        Assert.Equal(0f, D((int)B + 1, H / 2).X);
    }

    [Fact]
    public void MapIsLeftRightAndTopBottomSymmetric()
    {
        for (int y = 0; y < H; y += 7)
        for (int x = 0; x < W; x += 7)
        {
            var a = D(x, y);
            var mirrorX = D(W - 1 - x, y);
            var mirrorY = D(x, H - 1 - y);
            Assert.Equal(a.X, -mirrorX.X, 4);
            Assert.Equal(a.Y, mirrorX.Y, 4);
            Assert.Equal(a.X, mirrorY.X, 4);
            Assert.Equal(a.Y, -mirrorY.Y, 4);
        }
    }

    [Fact]
    public void CornersPointDiagonallyInwardAndStayWithinUnitRange()
    {
        var (x, y) = D(1, 1);
        Assert.True(x > 0 && y > 0);
        Assert.InRange(MathF.Sqrt(x * x + y * y), 0f, 1.0001f);
        var (bx, by) = D(W - 2, H - 2);
        Assert.True(bx < 0 && by < 0);
    }

    [Fact]
    public void GeneratedBytesEncodeNeutralAndDirection()
    {
        byte[] map = BezelMap.Generate(W, H, R, B);
        Assert.Equal(W * H * 4, map.Length);

        int center = ((H / 2) * W + W / 2) * 4;
        Assert.Equal(BezelMap.Neutral, map[center + 1]);
        Assert.Equal(BezelMap.Neutral, map[center + 2]);

        int left = ((H / 2) * W + 0) * 4;
        Assert.True(map[left + 2] > BezelMap.Neutral + 100);   // R: +x
        Assert.Equal(BezelMap.Neutral, map[left + 1]);          // G: no y at the left-edge midpoint

        for (int i = 3; i < map.Length; i += 4)
            Assert.Equal(255, map[i]);
    }
}
