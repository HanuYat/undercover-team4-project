using System;
using System.Collections.Generic;
using Unity.Netcode;

/// <summary>
/// 외형 특징 축 — 몽타주로 구두 전달 가능한 이산 특징의 종류. (#74)
/// 축별 옵션(표시 이름·시각 리소스)은 AppearanceDatabase가 정의한다.
/// </summary>
public enum AppearanceAxis
{
    HairColor = 0,
    FacialHair = 1,
    Accessory = 2,
}

/// <summary>
/// NPC 한 명의 외형 특징 조합 — 축별 옵션 인덱스만 담는다. (#74)
/// CitizenIdentity의 일부로 취급하며, 네트워크로는 이 인덱스만 전송된다
/// (#56 NpcAppearance의 서버 권위 인덱스 동기화 위에 확장).
/// </summary>
[Serializable]
public struct AppearanceProfile : INetworkSerializable, IEquatable<AppearanceProfile>
{
    /// <summary>외형 축 개수 — AppearanceAxis enum 값 수와 일치해야 한다.</summary>
    public const int k_axisCount = 3;

    public int HairColorIndex;
    public int FacialHairIndex;
    public int AccessoryIndex;

    /// <summary>아직 배정되지 않음(프리팹 기본 외형)을 뜻하는 값.</summary>
    public static AppearanceProfile Unassigned => new AppearanceProfile
    {
        HairColorIndex = -1,
        FacialHairIndex = -1,
        AccessoryIndex = -1,
    };

    public bool IsAssigned => HairColorIndex >= 0;

    public int GetIndex(AppearanceAxis axis) => axis switch
    {
        AppearanceAxis.HairColor => HairColorIndex,
        AppearanceAxis.FacialHair => FacialHairIndex,
        AppearanceAxis.Accessory => AccessoryIndex,
        _ => -1,
    };

    public void SetIndex(AppearanceAxis axis, int value)
    {
        switch (axis)
        {
            case AppearanceAxis.HairColor: HairColorIndex = value; break;
            case AppearanceAxis.FacialHair: FacialHairIndex = value; break;
            case AppearanceAxis.Accessory: AccessoryIndex = value; break;
        }
    }

    /// <summary>지정한 축들에서 다른 프로필과 값이 모두 같은지 — 몽타주 부합 판정에 사용.</summary>
    public bool MatchesOn(in AppearanceProfile other, IReadOnlyList<AppearanceAxis> axes)
    {
        for (int i = 0; i < axes.Count; i++)
        {
            if (GetIndex(axes[i]) != other.GetIndex(axes[i]))
                return false;
        }
        return true;
    }

    public void NetworkSerialize<TBuffer>(BufferSerializer<TBuffer> serializer) where TBuffer : IReaderWriter
    {
        serializer.SerializeValue(ref HairColorIndex);
        serializer.SerializeValue(ref FacialHairIndex);
        serializer.SerializeValue(ref AccessoryIndex);
    }

    public bool Equals(AppearanceProfile other) =>
        HairColorIndex == other.HairColorIndex
        && FacialHairIndex == other.FacialHairIndex
        && AccessoryIndex == other.AccessoryIndex;

    public override bool Equals(object obj) => obj is AppearanceProfile other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(HairColorIndex, FacialHairIndex, AccessoryIndex);
}
