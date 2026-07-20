using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 계정 상태 패널 — 우측 상단에 로그인된 PlayerId, 우측 하단에 로그인/로그아웃 버튼. (#247)
/// 익명 로그인 자체는 AuthBootstrap이 씬 시작 시 자동 수행(m_signInOnStart)하고,
/// 이 패널은 상태 표시와 수동 로그인/로그아웃 진입점만 제공한다.
/// </summary>
public class AuthPanel : PanelBase
{
    public override bool CanCloseWithESC => false;
    public override bool IsStackable => false;
    protected override bool OpenOnAwake => true;

    [Header("UI 참조")]
    [SerializeField] private TMP_Text m_playerIdText; // 우측 상단
    [SerializeField] private Button m_signInButton;   // 우측 하단
    [SerializeField] private Button m_signOutButton;

    private AuthBootstrap Auth => App.Net.Auth;

    private bool m_isSigningIn; // 로그인 요청 겹침 방지 래치

    private void OnEnable()
    {
        m_signInButton.onClick.AddListener(HandleSignInClicked);
        m_signOutButton.onClick.AddListener(HandleSignOutClicked);

        if (Auth != null)
        {
            Auth.OnSignedIn += Refresh;
            Auth.OnSignedOut += Refresh;
        }

        Refresh();
    }

    private void OnDisable()
    {
        m_signInButton.onClick.RemoveListener(HandleSignInClicked);
        m_signOutButton.onClick.RemoveListener(HandleSignOutClicked);

        if (Auth != null)
        {
            Auth.OnSignedIn -= Refresh;
            Auth.OnSignedOut -= Refresh;
        }
    }

    private void HandleSignInClicked() => SignInAsync().Forget();

    private async UniTaskVoid SignInAsync()
    {
        if (m_isSigningIn || Auth == null)
            return;
        m_isSigningIn = true;
        try
        {
            await Auth.InitializeAndSignInAsync();
        }
        finally
        {
            m_isSigningIn = false;
            Refresh();
        }
    }

    // 세션 참가 중·전환 중 로그아웃 거부는 AuthBootstrap.SignOut 내부 가드가 처리한다
    private void HandleSignOutClicked()
    {
        if (Auth != null)
            Auth.SignOut();
        Refresh();
    }

    private void Refresh()
    {
        bool signedIn = Auth != null && Auth.IsSignedIn;
        m_playerIdText.text = signedIn ? $"ID: {Auth.PlayerId}" : "로그인 안 됨";
        m_signInButton.interactable = !signedIn;
        m_signOutButton.interactable = signedIn;
    }
}
