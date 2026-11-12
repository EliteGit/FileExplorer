namespace FileExplorer.Core.Treemap;

public readonly record struct RectD(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double Area => W * H;
    public bool Contains(double x, double y) => x >= X && x < X + W && y >= Y && y < Y + H;
    public RectD Deflate(double d) => W <= 2 * d || H <= 2 * d ? new RectD(X, Y, 0, 0) : new RectD(X + d, Y + d, W - 2 * d, H - 2 * d);
}

/// <summary>Squarified treemap: a pure function from weights and a rectangle to one rectangle per weight (same order).</summary>
public static class SquarifiedLayout
{
    public static RectD[] Layout(IReadOnlyList<double> weights, RectD bounds)
    {
        var result = new RectD[weights.Count];
        var order = Enumerable.Range(0, weights.Count).Where(i => weights[i] > 0).OrderByDescending(i => weights[i]).ToList();
        double total = order.Sum(i => weights[i]);
        if (order.Count == 0 || total <= 0 || bounds.W <= 0 || bounds.H <= 0) return result;

        double scale = bounds.Area / total;
        double x = bounds.X, y = bounds.Y, w = bounds.W, h = bounds.H;
        int pos = 0;
        while (pos < order.Count)
        {
            double side = Math.Min(w, h);
            int end = pos + 1;
            double sum = weights[order[pos]] * scale;
            double worst = Worst(weights, order, pos, end, scale, sum, side);
            while (end < order.Count)
            {
                double nextSum = sum + weights[order[end]] * scale;
                double next = Worst(weights, order, pos, end + 1, scale, nextSum, side);
                if (next > worst) break;
                sum = nextSum; worst = next; end++;
            }
            PlaceRow(weights, order, pos, end, scale, sum, ref x, ref y, ref w, ref h, result);
            pos = end;
        }
        return result;
    }

    private static double Worst(IReadOnlyList<double> weights, List<int> order, int from, int to, double scale, double sum, double side)
    {
        double max = double.MinValue, min = double.MaxValue;
        for (int k = from; k < to; k++)
        {
            double a = weights[order[k]] * scale;
            max = Math.Max(max, a); min = Math.Min(min, a);
        }
        return Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
    }

    private static void PlaceRow(IReadOnlyList<double> weights, List<int> order, int from, int to, double scale, double sum,
        ref double x, ref double y, ref double w, ref double h, RectD[] result)
    {
        if (w >= h)
        {
            double colW = sum / h, cy = y;
            for (int k = from; k < to; k++)
            {
                double ch = weights[order[k]] * scale / colW;
                result[order[k]] = new RectD(x, cy, colW, ch); cy += ch;
            }
            x += colW; w -= colW;
        }
        else
        {
            double rowH = sum / w, cx = x;
            for (int k = from; k < to; k++)
            {
                double cw = weights[order[k]] * scale / rowH;
                result[order[k]] = new RectD(cx, y, cw, rowH); cx += cw;
            }
            y += rowH; h -= rowH;
        }
    }
}
