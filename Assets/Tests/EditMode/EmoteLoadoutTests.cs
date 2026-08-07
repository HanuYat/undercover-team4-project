using NUnit.Framework;

/// <summary>
/// 로비에서 구성한 8칸 배치의 저장·복원. 사용자가 공들여 맞춘 구성이 조용히 날아가는 것이
/// 이 클래스에서 나올 수 있는 가장 나쁜 버그라 왕복을 테스트로 못박는다. (#219)
/// </summary>
public class EmoteLoadoutTests
{
    [Test]
    public void 새_구성은_전부_비어있다()
    {
        var loadout = new EmoteLoadout();
        for (int slot = 0; slot < EmoteLoadout.k_slotCount; slot++)
            Assert.IsNull(loadout.GetSlot(slot), $"{slot}번 칸");
    }

    [Test]
    public void 넣은_값을_그대로_돌려준다()
    {
        var loadout = new EmoteLoadout();
        loadout.SetSlot(3, "dance01");
        Assert.AreEqual("dance01", loadout.GetSlot(3));
    }

    [Test]
    public void 빈_문자열은_빈_칸으로_취급한다()
    {
        var loadout = new EmoteLoadout();
        loadout.SetSlot(2, "cheer01");
        loadout.SetSlot(2, "");
        Assert.IsNull(loadout.GetSlot(2));
    }

    [Test]
    public void 범위_밖_인덱스는_무시한다()
    {
        var loadout = new EmoteLoadout();
        Assert.DoesNotThrow(() => loadout.SetSlot(-1, "dance01"));
        Assert.DoesNotThrow(() => loadout.SetSlot(EmoteLoadout.k_slotCount, "dance01"));
        Assert.IsNull(loadout.GetSlot(-1));
        Assert.IsNull(loadout.GetSlot(EmoteLoadout.k_slotCount));
    }

    [Test]
    public void 직렬화_왕복이_구성을_보존한다()
    {
        var source = new EmoteLoadout();
        source.SetSlot(0, "dance01");
        source.SetSlot(4, "cheer01");
        source.SetSlot(7, "clap01");

        var restored = new EmoteLoadout();
        restored.Deserialize(source.Serialize());

        Assert.AreEqual("dance01", restored.GetSlot(0));
        Assert.IsNull(restored.GetSlot(1));
        Assert.AreEqual("cheer01", restored.GetSlot(4));
        Assert.AreEqual("clap01", restored.GetSlot(7));
    }

    [Test]
    public void 짧은_문자열을_읽어도_칸_수는_그대로다()
    {
        // 저장 포맷이 바뀌었거나 파일이 잘린 경우 — 남은 칸은 비워 두고 살아남아야 한다
        var loadout = new EmoteLoadout();
        loadout.Deserialize("dance01|cheer01");

        Assert.AreEqual("dance01", loadout.GetSlot(0));
        Assert.AreEqual("cheer01", loadout.GetSlot(1));
        Assert.IsNull(loadout.GetSlot(7));
    }

    [Test]
    public void 긴_문자열은_넘치는_부분을_버린다()
    {
        var loadout = new EmoteLoadout();
        loadout.Deserialize("a|b|c|d|e|f|g|h|i|j");

        Assert.AreEqual("h", loadout.GetSlot(7), "마지막 칸");
        Assert.IsNull(loadout.GetSlot(EmoteLoadout.k_slotCount), "칸 수를 넘은 자리");
        // 넘친 값이 8칸 안으로 밀려 들어오지 않았는지 — 직렬화하면 8칸치만 나와야 한다
        Assert.AreEqual("a|b|c|d|e|f|g|h", loadout.Serialize());
    }

    [Test]
    public void 빈_문자열을_읽으면_전부_빈_칸이다()
    {
        var loadout = new EmoteLoadout();
        loadout.SetSlot(0, "dance01");
        loadout.Deserialize("");

        Assert.IsNull(loadout.GetSlot(0));
    }

    [Test]
    public void null을_읽어도_예외가_없다()
    {
        var loadout = new EmoteLoadout();
        Assert.DoesNotThrow(() => loadout.Deserialize(null));
    }
}
