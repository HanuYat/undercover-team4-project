using System;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 닉네임 편집 표시부 — 세션 화면(SessionPanel 하위)에 상시 노출된다. (#585)
///
/// <b>옛 AuthPanel에서 떼어낸 것이다.</b> 세션에 들어가기 전에 이름을 고치는 것이 이 화면의
/// 주 목적 중 하나라 계정 기능과 같은 자리에 둘 수 없었다. 그 AuthPanel은 이후 로그아웃
/// 버튼 하나만 남아 <see cref="SignOutView"/>가 됐다.
///
/// PanelBase가 아니라 일반 MonoBehaviour다 — 따로 열고 닫을 일이 없고 부모(세션 화면)와
/// 생사를 함께한다. OnEnable/OnDisable로 구독을 관리하므로 부모가 꺼지면 함께 조용해진다.
/// (ScanInfoView·TeamFundBalanceView와 같은 자리)
/// </summary>
public class NicknameView : MonoBehaviour
{
    [SerializeField]
    private TMP_InputField m_nicknameInput;

    [SerializeField]
    private Button m_applyButton;

    [SerializeField]
    private TMP_Text m_statusText;

    private bool m_isApplying;

    // 마지막으로 띄운 사유 — 문장이 아니라 키로 들고 있어야 언어가 바뀔 때 다시 읽을 수 있다 (#497)
    private LocalizedMessage m_status;

    private AuthBootstrap Auth => App.Net.Auth;

    private void OnEnable()
    {
        m_applyButton.onClick.AddListener(HandleApplyClicked);

        // 상한을 인스펙터에 중복 입력하지 않는다 — NicknameRules의 상수가 단일 출처 (#249)
        m_nicknameInput.characterLimit = NicknameRules.MaxLength;

        if (Auth != null)
        {
            Auth.OnSignedIn += Refresh;
            Auth.OnSignedOut += Refresh;
            Auth.OnNicknameChanged += Refresh;
        }

        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
        Refresh();
    }

    private void OnDisable()
    {
        m_applyButton.onClick.RemoveListener(HandleApplyClicked);

        if (Auth != null)
        {
            Auth.OnSignedIn -= Refresh;
            Auth.OnSignedOut -= Refresh;
            Auth.OnNicknameChanged -= Refresh;
        }

        // 종료 중에는 설정 에셋을 되살리지 않는다 — HasSettings로 먼저 확인한다 (ShopStand 관례)
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    private void HandleLocaleChanged(Locale locale)
    {
        RenderStatus();
        Refresh();
    }

    private void SetStatus(in LocalizedMessage message)
    {
        m_status = message;
        RenderStatus();
    }

    private void RenderStatus()
    {
        if (m_statusText != null)
            m_statusText.text = m_status.Resolve();
    }

    private void HandleApplyClicked() => ApplyAsync().Forget();

    private async UniTaskVoid ApplyAsync()
    {
        if (m_isApplying || Auth == null)
            return;

        m_isApplying = true;
        string attempted = m_nicknameInput.text;
        bool failed = false;
        try
        {
            await Auth.SetPlayerNameAsync(attempted);
            SetStatus(LocalizedMessage.None);
        }
        catch (LocalizedMessageException ex)
        {
            // 규칙 위반 — 사유가 키로 온다 (#497)
            failed = true;
            SetStatus(ex.Reason);
        }
        catch (Exception ex)
        {
            // UGS가 준 문구는 우리 테이블에 없다 — 그대로 띄우고 번역 대상에서 뺀다
            failed = true;
            SetStatus(LocalizedMessage.Literal(ex.Message));
        }
        finally
        {
            m_isApplying = false;
            Refresh();
            if (failed)
                m_nicknameInput.text = attempted; // 거절 사유를 보며 고칠 수 있게 남긴다
        }
    }

    private void Refresh()
    {
        bool signedIn = Auth != null && Auth.IsSignedIn;

        // 세션에 들어간 뒤 PlayerName이 바뀌면 로비·Vivox가 옛 이름을 들고 어긋난다
        bool canEdit = signedIn && !Auth.IsNetworkConnected && !m_isApplying;
        m_nicknameInput.interactable = canEdit;
        m_applyButton.interactable = canEdit;

        // 입력 중 덮어쓰지 않음.
        if (!m_nicknameInput.isFocused)
            m_nicknameInput.text = signedIn ? Auth.Nickname : string.Empty;
    }
}
