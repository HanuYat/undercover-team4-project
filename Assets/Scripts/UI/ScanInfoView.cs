using TMPro;
using UnityEngine;

/// <summary>
/// NPC 머리 위에 붙는 월드공간 스캔 정보 카드. (#233)
/// NPC 프리팹의 자식으로 배치되어 부모-자식 Transform으로 위치를 따라간다 — 별도 위치 갱신 로직 불필요.
/// 표시 내용(실제/??)은 각 클라이언트의 ScanResultPresenter가 로컬로 세팅한다 —
/// 네트워크 동기화 없음(개인별 관리, GDD 5-4). NPC는 각 클라마다 로컬 인스턴스가 있으므로
/// 클라 A는 실제값, 클라 B는 ??를 각자 자기 화면의 같은 NPC에 독립적으로 찍을 수 있다.
/// 기본은 비활성 — 로컬 플레이어가 스캐너로 조준할 때만 프레젠터가 켠다.
/// </summary>
public class ScanInfoView : MonoBehaviour
{
    [Header("텍스트")]
    [SerializeField]
    private TMP_Text m_nameText;

    [SerializeField]
    private TMP_Text m_typeText;

    [SerializeField]
    private TMP_Text m_factionText;

    [Header("빌보드")]
    [Tooltip("켜져 있는 동안 카메라를 향하도록 회전한다. 끄면 프리팹의 고정 방향을 유지")]
    [SerializeField]
    private bool m_billboard = true;

    // 미스캔 NPC의 미확인 필드 표기 (#233)
    private const string k_masked = "??";

    private Transform m_cameraTransform;

    /// <summary>
    /// 빌보드가 바라볼 카메라를 지정한다. 프레젠터가 로컬 플레이어 카메라(PlayerInteractor.AimCamera)를
    /// 넘겨준다 — Camera.main에 의존하면 플레이어 카메라에 MainCamera 태그가 없을 때 회전이 멈춘다.
    /// </summary>
    public void SetCamera(Transform cameraTransform)
    {
        if (cameraTransform != null)
        {
            m_cameraTransform = cameraTransform;
        }
    }

    /// <summary>스캔 완료 NPC — 실제 프로필 값을 표시하고 카드를 켠다.</summary>
    public void ShowReal(string citizenName, string typeView, string factionView)
    {
        m_nameText.text = $"Name: {citizenName}";
        m_typeText.text = $"Type: {typeView}";
        m_factionText.text = $"Faction: {factionView}";
        SetCardActive(true);
    }

    /// <summary>미스캔 NPC — 모든 필드를 ??로 마스킹하고 카드를 켠다.</summary>
    public void ShowMasked()
    {
        m_nameText.text = $"Name: {k_masked}";
        m_typeText.text = $"Type: {k_masked}";
        m_factionText.text = $"Faction: {k_masked}";
        SetCardActive(true);
    }

    /// <summary>카드를 끈다.</summary>
    public void Hide()
    {
        SetCardActive(false);
    }

    private void SetCardActive(bool active)
    {
        if (gameObject.activeSelf != active)
        {
            gameObject.SetActive(active);
        }
    }

    // 켜져 있을 때만 돈다(비활성이면 호출되지 않음). 카드의 회전을 카메라 회전에 맞춰(forward·up 동일)
    // 화면과 평행하게 만든다 — UI 캔버스는 이 방향에서 글자가 반전 없이 정면으로 읽힌다.
    private void LateUpdate()
    {
        if (!m_billboard)
        {
            return;
        }

        if (m_cameraTransform == null)
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            m_cameraTransform = camera.transform;
        }

        transform.rotation = Quaternion.LookRotation(m_cameraTransform.forward, m_cameraTransform.up);
    }
}
