using GameKouryakuKun.Core.Throughput;
using GameKouryakuKun.Storage;
using Xunit;

namespace GameKouryakuKun.Core.Tests;

public class ThroughputT4Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gkk-rawagg-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); } catch { } }

    private static RawExperience R(long seq, string before, string act, string after, bool c, string ev = "{}") =>
        new(seq, (int)seq, seq * 10, seq * 100, seq * 1000.0, before, act, after, c, "normal", ev);

    [Fact]
    public void Aggregate_counts_duplicates_but_raw_keeps_every_row()
    {
        using var st = RawAggStore.Open(_dir, "test");
        Assert.False(st.Append(R(1, "m1|a", "b1", "m2|x", true, "{\"frames\":10}")));
        Assert.True(st.Append(R(2, "m1|a", "b1", "m2|x", true, "{\"frames\":30}")));   // 今の表現では同じ
        Assert.False(st.Append(R(3, "m1|a", "b1", "m1|a", false)));                    // 結果が違えば別の行（確率的な反応を失わない）
        Assert.Equal(3, st.RawCount());
        Assert.Equal(2, st.Index.Keys);
        Assert.Equal(1.0 / 3, st.Index.DuplicateRatio, 6);
        Assert.True(st.VerifyReplay());
    }

    [Fact]
    public void Raw_is_append_only()
    {
        using var st = RawAggStore.Open(_dir, "test");
        st.Append(R(1, "m1", "b1", "m2", true));
        Assert.ThrowsAny<Exception>(() => st.Db.Exec("UPDATE raw_experience SET action='x'"));
        Assert.ThrowsAny<Exception>(() => st.Db.Exec("DELETE FROM raw_experience"));
        Assert.Equal(1, st.RawCount());
    }

    [Fact]
    public void New_representation_reaggregates_from_raw_and_splits_what_v1_merged()
    {
        // v1 では同じだった 2 つの経験が、新しい特徴（根拠の frames）で別だったと分かる
        using var st = RawAggStore.Open(_dir, "test");
        st.Append(R(1, "m1", "b1", "m2", true, "{\"frames\":10}"));
        st.Append(R(2, "m1", "b1", "m2", true, "{\"frames\":30}"));
        Assert.Equal(1, st.Index.Keys);
        var v2 = st.Reaggregate(2, r => new AggKey(r.NodeBefore + "#" + r.EvidenceJson, r.Action, r.NodeAfter ?? "-"));
        Assert.Equal(2, v2.Keys);
        Assert.Equal(1, st.LoadAgg(1).Keys);   // 古い版は消さない
        Assert.Equal(2, st.LoadAgg(2).Keys);
        Assert.Throws<ArgumentException>(() => st.Reaggregate(1, AggregateIndex.SignatureV1));
    }

    [Fact]
    public void Reopen_restores_index_from_raw()
    {
        using (var st = RawAggStore.Open(_dir, "test")) { st.Append(R(1, "m1", "b1", "m2", true)); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var st2 = RawAggStore.Open(_dir, "test");
        Assert.True(st2.Append(R(2, "m1", "b1", "m2", true)));   // 前の実行の経験も既出として数える
        Assert.Equal(1.0, st2.Index.DuplicateRatio);
        Assert.True(st2.VerifyReplay());
    }

    [Fact]
    public void Rebuild_is_deterministic_regardless_of_input_order()
    {
        var raw = new[] { R(2, "a", "x", "b", true), R(1, "a", "x", "b", true), R(3, "b", "y", "a", false) };
        var a = AggregateIndex.Rebuild(raw, 1, AggregateIndex.SignatureV1);
        var b = AggregateIndex.Rebuild(raw.Reverse(), 1, AggregateIndex.SignatureV1);
        Assert.True(a.SameAs(b));
        Assert.Equal(1, a.Entries[AggregateIndex.SignatureV1(raw[0])].FirstSeq);
    }
}
