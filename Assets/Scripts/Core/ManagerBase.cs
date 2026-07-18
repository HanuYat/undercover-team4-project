using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// App의 매니저 필드에 매니저 인스턴스를 자동 주입/해제하는 리플렉션 헬퍼.
/// 매니저는 CommonManagerBase 또는 NetworkedManagerBase를 상속하는 것만으로 App에 등록된다.
/// App의 private 필드는 이 클래스가 리플렉션으로 쓴다 — 필드에 직접 대입하는 코드를 만들지 말 것.
/// </summary>
public static class ManagerHandler
{
    private static readonly Dictionary<Type, FieldInfo> s_appFields;

    static ManagerHandler()
    {
        FieldInfo[] fields = typeof(App).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        s_appFields = new Dictionary<Type, FieldInfo>(fields.Length);

        foreach (FieldInfo field in fields)
        {
            if (!s_appFields.TryAdd(field.FieldType, field))
                Debug.LogError($"[ManagerHandler] App에 같은 타입 필드가 중복됨: {field.FieldType.Name}");
        }
    }

    internal static void Register(MonoBehaviour manager)
    {
        FieldInfo field = FindField(manager.GetType());
        if (field == null)
        {
            Debug.LogError($"[ManagerHandler] App에 대응 필드가 없는 매니저: {manager.GetType().Name}");
            return;
        }

        // 살아 있는 다른 인스턴스를 조용히 덮어쓰지 않는다 — 같은 씬 중복 배치 사고를 즉시 드러낸다
        if (field.GetValue(App.Instance) is MonoBehaviour current && current != null && current != manager)
            Debug.LogError($"[ManagerHandler] {field.FieldType.Name} 중복 등록: '{current.name}' 위에 '{manager.name}'. 씬에 같은 매니저가 2개인지 확인.");

        field.SetValue(App.Instance, manager);
    }

    internal static void Unregister(MonoBehaviour manager)
    {
        FieldInfo field = FindField(manager.GetType());
        if (field == null)
            return;

        // 내가 등록한 값일 때만 지운다 — 새 씬의 매니저가 먼저 등록된 경우를 덮어쓰지 않는다
        if (ReferenceEquals(field.GetValue(App.Instance), manager))
            field.SetValue(App.Instance, null);
    }

    private static FieldInfo FindField(Type type)
    {
        // 정확한 타입 우선, 없으면 대입 가능한 부모 타입 필드 — 단 후보가 정확히 하나일 때만
        if (s_appFields.TryGetValue(type, out FieldInfo exact))
            return exact;

        FieldInfo found = null;
        foreach (FieldInfo field in s_appFields.Values)
        {
            if (!field.FieldType.IsAssignableFrom(type))
                continue;
            if (found != null)
                return null; // 두 필드에 대입 가능 — 모호하므로 실패 처리
            found = field;
        }
        return found;
    }
}

/// <summary>일반 매니저 베이스. Awake에서 App에 등록, 파괴 시 해제.</summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public abstract class CommonManagerBase : MonoBehaviour
{
    protected virtual void Awake() => ManagerHandler.Register(this);
    protected virtual void OnDestroy() => ManagerHandler.Unregister(this);
}

/// <summary>
/// NetworkBehaviour 매니저 베이스 (SuddenEventManager·WantedListManager용).
/// 등록 로직은 CommonManagerBase와 동일 — NGO가 OnDestroy를 가상으로 제공하므로 base 호출을 유지한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public abstract class NetworkedManagerBase : NetworkBehaviour
{
    protected virtual void Awake() => ManagerHandler.Register(this);

    public override void OnDestroy()
    {
        ManagerHandler.Unregister(this);
        base.OnDestroy();
    }
}
