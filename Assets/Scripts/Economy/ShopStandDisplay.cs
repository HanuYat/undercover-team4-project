using UnityEngine;

/// <summary>진열 모델을 런타임에 앵커에 올린다 (#814). 순수 로컬 표현 — 네트워크 스폰하지 않는다.</summary>
public class ShopStandDisplay : MonoBehaviour
{
    [Tooltip("모델을 붙일 자식 앵커. 비우면 자기 자신")]
    [SerializeField]
    private Transform m_anchor;

    private GameObject m_instance;

    private void Awake()
    {
        if (m_anchor == null)
            m_anchor = transform;
    }

    /// <summary>이전 모델을 지우고 entry가 지정한 모델로 교체한다. entry가 null이면 비운다.</summary>
    public void Rebuild(ShopCatalog.Entry entry)
    {
        if (m_instance != null)
        {
            Destroy(m_instance);
            m_instance = null;
        }

        GameObject model = entry?.DisplayModel;
        if (model == null)
            return;

        m_instance = Instantiate(model, m_anchor);
        m_instance.transform.localPosition = Vector3.zero;
        m_instance.transform.localRotation = Quaternion.Euler(entry.DisplayEuler);
        m_instance.transform.localScale = entry.DisplayScale;

        // 표시 전용 — 조준 판정은 ShopStand의 고정 콜라이더가 맡는다.
        foreach (Collider modelCollider in m_instance.GetComponentsInChildren<Collider>(true))
            Destroy(modelCollider);
    }
}
