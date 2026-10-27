using GameKouryakuKun.Core.Exploration;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class BlockV026ChordTests
{
    [Fact]
    public void Untried_chords_wait_until_singles_are_tried_on_that_screen()
    {
        var acts = Explorer.FromLogicalKeys(new[] { "Up", "Down" }).ToList();
        var ex = new Explorer(acts, 1);
        var up = acts.First(a => a.Name == "Up"); var dn = acts.First(a => a.Name == "Down");
        var ch = Explorer.MakeChord(up, dn);
        ex.Observe("other", ch, "z", true);    // 別の画面で変化を起こした同時押し → 候補に加わる
        Assert.DoesNotContain(ex.Choose("s").Candidates, c => c.Action == ch.Name);
        for (int i = 0; i < 2; i++) { ex.Observe("s", up, "s", false); ex.Observe("s", dn, "s", false); }
        Assert.Contains(ex.Choose("s").Candidates, c => c.Action == ch.Name);
        Assert.Contains(ex.Choose("other").Candidates, c => c.Action == ch.Name);   // 既に試した画面では候補のまま
    }
}
