using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 치장 뽑기 릴 (#818 D) — 아이콘이 좌로 흘러가다 감속해 <b>가운데 칸에 당첨이 멈춘다</b>.
///
/// 릴은 <b>칸을 재활용한다</b>: 화면에 보이는 만큼(<see cref="m_visibleCells"/>)만 만들어 두고, 흘러간
/// 거리에서 각 칸이 무엇을 비출지 계산한다. 지나가는 칸 수만큼 오브젝트를 찍으면 서른 개가 넘고
/// 그중 대부분이 한 번 스쳐 지나갈 뿐이다.
///
/// <b>결과는 이미 정해져 있다.</b> 자판기가 뽑기와 장부를 끝낸 뒤 이 창에 넘긴다 — 릴은 그 답이
/// 가운데에 오도록 감속 곡선의 끝점을 맞추는 연출일 뿐이다. 연출 도중 창을 닫아도 잔량은 옳다.
///
/// <b>겹치는 창이 아니라 잠깐 뜨는 덮개다</b> — ESC로 닫지 않고 스택에도 쌓지 않는다.
/// 몇 초 뒤 스스로 닫히므로 스택에 쌓으면 그 사이 ESC 한 번이 이 창을 먹는다.
/// </summary>
public class CosmeticGachaPanel : PanelBase
{
    [Header("배선")]
    [Tooltip("치장 카탈로그 — 자판기와 같은 에셋을 물릴 것")]
    [SerializeField] private AccessoryCatalog m_catalog;

    [Tooltip("칸이 놓이는 띠 — 잘리는 창(RectMask2D)의 자식이어야 한다")]
    [SerializeField] private RectTransform m_strip;

    [Tooltip("가운데 당첨 칸 테두리 — 멈추는 순간 색이 바뀐다")]
    [SerializeField] private Graphic[] m_frame;

    [Tooltip("결과 문구")]
    [SerializeField] private TMP_Text m_resultLabel;

    [Header("릴")]
    [Tooltip("한 칸의 한 변(px)")]
    [SerializeField] private float m_cellSize = 128f;

    [Tooltip("칸 사이 간격(px)")]
    [SerializeField] private float m_gap = 12f;

    [Tooltip("동시에 만드는 칸 수 — 홀수여야 가운데가 생긴다. 양 끝 두 칸은 잘려 보인다")]
    [SerializeField] private int m_visibleCells = 7;

    [Tooltip("멈추기까지 지나가는 칸 수 — 클수록 길게 돈다")]
    [SerializeField] private int m_scrollCells = 32;

    [Tooltip("도는 시간(초)")]
    [SerializeField] private float m_spinSeconds = 2.6f;

    [Tooltip("멈춘 뒤 결과를 보여 주는 시간(초)")]
    [SerializeField] private float m_holdSeconds = 1.8f;

    [Header("문구")]
    [Tooltip("새로 얻었을 때 — {0}에 이름이 들어간다")]
    [SerializeField] private LocalizedString m_resultFormat;

    [Tooltip("이미 가진 것이었을 때 — {0}에 이름이 들어간다")]
    [SerializeField] private LocalizedString m_duplicateFormat;

    [Header("소리")]
    [Tooltip("릴이 도는 동안 나는 소리 — 멈추는 순간 끊긴다")]
    [SerializeField] private EAudioClip m_spinSound = EAudioClip.GachaSpin;

    [Tooltip("당첨이 가운데 멈춘 순간 나는 소리")]
    [SerializeField] private EAudioClip m_revealSound = EAudioClip.GachaReveal;

    [Header("색")]
    [SerializeField] private Color m_frameIdle = new Color(1f, 1f, 1f, 0.35f);
    [SerializeField] private Color m_frameWin = new Color(1f, 0.85f, 0.2f, 1f);

    public override bool CanCloseWithESC => false;
    public override bool IsStackable => false;

    private readonly List<Cell> m_cells = new List<Cell>();
    private readonly List<(EAccessorySlot Slot, int Index)> m_sequence =
        new List<(EAccessorySlot Slot, int Index)>();
    private readonly List<(EAccessorySlot Slot, int Index)> m_pool =
        new List<(EAccessorySlot Slot, int Index)>();

    private bool m_spinning;

    // 릴 소리 전용 소스 — 효과음 풀은 오래된 것을 뺏어 가므로 붙잡고 있다 끊을 수 없다
    private AudioSource m_spinSource;

    /// <summary>릴이 도는 중인가 — 자판기가 겹쳐 돌리지 않으려고 본다.</summary>
    public bool IsSpinning => m_spinning;

    /// <summary>릴이 도는 시간(초) — 자판기가 남의 화면 연출을 여기에 맞춘다 (#850).</summary>
    public float SpinSeconds => m_spinSeconds;

    private float Pitch => m_cellSize + m_gap;
    private int Center => VisibleCells / 2;

    // 인스펙터 값이 이상해도 릴이 터지지 않게 못 박는다. 칸 수가 0 이하면 당첨을 꽂는 자리가
    // 시퀀스 범위를 넘고, 짝수면 가운데 칸이 테두리와 반 칸 어긋난다(툴팁 경고만으로는 못 막는다).
    private int VisibleCells => Mathf.Max(3, m_visibleCells | 1);
    private int ScrollCells => Mathf.Max(1, m_scrollCells);

    protected override void Awake()
    {
        base.Awake();

        // 음원이 도는 시간보다 길어 멈추는 순간 잘라야 한다 (SoundManager.GetSfxEntry 참고)
        m_spinSource = gameObject.AddComponent<AudioSource>();
        m_spinSource.playOnAwake = false;
        m_spinSource.loop = true; // 도는 시간을 늘려 배선해도 소리가 먼저 끝나지 않게
        m_spinSource.spatialBlend = 0f; // 2D — 릴은 뽑은 사람 화면에만 뜬다
    }

    /// <summary>
    /// 이 결과가 가운데에 멈추도록 릴을 돌린다 (#818 D).
    /// <paramref name="gained"/>가 false면 중복이라 환급된 경우의 문구가 나간다.
    /// </summary>
    public void Play(EAccessorySlot slot, int index, bool gained)
    {
        if (m_spinning || m_catalog == null || m_strip == null)
            return;

        BuildPool();
        if (m_pool.Count == 0)
            return;

        BuildSequence(slot, index);
        EnsureCells();
        SetFrameColor(m_frameIdle);

        if (m_resultLabel != null)
            m_resultLabel.text = string.Empty;

        OpenPanel();
        SpinAsync(slot, index, gained).Forget();
    }

    // 릴에 흘려보낼 후보 — 카탈로그 전체다. 슬롯이 섞여 흐르는 것이 뽑기판처럼 보이기도 하고,
    // 실제 뽑기도 전체 풀에서 균등하므로 보이는 것과 뽑히는 것이 어긋나지 않는다.
    private void BuildPool()
    {
        if (m_pool.Count > 0)
            return;

        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            int count = m_catalog.CountOf(slot);
            for (int i = 1; i < count; i++)
                if (m_catalog.Get(slot, i) != null)
                    m_pool.Add((slot, i));
        }
    }

    // 당첨은 <b>정확히 ScrollCells + Center</b> 자리에 둔다 — 감속이 끝나는 순간 그 자리가 가운데다.
    private void BuildSequence(EAccessorySlot slot, int index)
    {
        m_sequence.Clear();
        int length = ScrollCells + VisibleCells;
        for (int i = 0; i < length; i++)
            m_sequence.Add(m_pool[UnityEngine.Random.Range(0, m_pool.Count)]);

        m_sequence[ScrollCells + Center] = (slot, index);
    }

    private void EnsureCells()
    {
        while (m_cells.Count < VisibleCells)
            m_cells.Add(CreateCell(m_cells.Count));

        for (int i = 0; i < m_cells.Count; i++)
            m_cells[i].Root.gameObject.SetActive(i < VisibleCells);
    }

    // 칸은 런타임에 찍는다 — 개수가 인스펙터 값이라 프리팹에 미리 박아 둘 수 없다
    private Cell CreateCell(int order)
    {
        var root = new GameObject($"Cell {order}", typeof(RectTransform)).GetComponent<RectTransform>();
        root.SetParent(m_strip, false);
        root.anchorMin = new Vector2(0.5f, 0.5f);
        root.anchorMax = new Vector2(0.5f, 0.5f);
        root.pivot = new Vector2(0.5f, 0.5f);
        root.sizeDelta = new Vector2(m_cellSize, m_cellSize);

        var icon = new GameObject("Icon", typeof(RectTransform)).AddComponent<Image>();
        var iconRect = icon.rectTransform;
        iconRect.SetParent(root, false);
        iconRect.anchorMin = Vector2.zero;
        iconRect.anchorMax = Vector2.one;
        iconRect.offsetMin = Vector2.zero;
        iconRect.offsetMax = Vector2.zero;
        icon.preserveAspect = true;
        icon.raycastTarget = false;

        // 아이콘이 아직 안 구워진 사람에게도 무엇이 흐르는지 보이게 이름을 뒤에 둔다
        // (Assets/Imported는 별도 저장소라 받지 않은 상태가 있을 수 있다)
        var label = new GameObject("Name", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
        var labelRect = label.rectTransform;
        labelRect.SetParent(root, false);
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = 16f;
        label.raycastTarget = false;
        labelRect.SetAsFirstSibling();

        return new Cell { Root = root, Icon = icon, Label = label };
    }

    private async UniTaskVoid SpinAsync(EAccessorySlot slot, int index, bool gained)
    {
        m_spinning = true;
        PlaySpinSound();
        try
        {
            float elapsed = 0f;
            while (elapsed < m_spinSeconds)
            {
                await UniTask.NextFrame(destroyCancellationToken);
                elapsed += Time.unscaledDeltaTime; // 정산·일시정지로 시간이 멈춰도 릴은 돈다

                // 끝으로 갈수록 느려지는 곡선. 끝점이 정확히 ScrollCells라 당첨이 가운데에 선다.
                float t = Mathf.Clamp01(elapsed / m_spinSeconds);
                Layout(ScrollCells * (1f - Mathf.Pow(1f - t, 3f)));
            }

            Layout(ScrollCells); // 부동소수 오차로 반 칸 어긋난 채 끝나지 않게 못 박는다
            StopSpinSound(); // 당첨음과 겹치지 않게 도는 소리를 먼저 끊는다
            App.Sound?.PlaySfx2D(m_revealSound);
            SetFrameColor(m_frameWin);
            ShowResult(slot, index, gained);

            await UniTask.Delay(
                TimeSpan.FromSeconds(m_holdSeconds),
                DelayType.UnscaledDeltaTime,
                cancellationToken: destroyCancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            // 씬이 넘어갔다 — 아래에서 닫기만 한다
        }
        finally
        {
            StopSpinSound(); // 연출이 도중에 끊겨도 소리는 남지 않는다
            m_spinning = false;

            // 취소는 이 창이 파괴됐다는 뜻이다 — PanelBase.ClosePanel은 m_panelRoot에 가드가 없어
            // 파괴된 오브젝트에 SetActive를 부른다. 여기서 걸러야 예외가 로그에 남지 않는다.
            if (m_panelRoot != null)
                ClosePanel();
        }
    }

    // 지나간 칸 수(passed)에서 각 칸이 무엇을 비추고 어디 있을지 정한다.
    private void Layout(float passed)
    {
        int start = Mathf.FloorToInt(passed);
        float fraction = passed - start;

        for (int i = 0; i < VisibleCells; i++)
        {
            Cell cell = m_cells[i];
            cell.Root.anchoredPosition = new Vector2((i - Center - fraction) * Pitch, 0f);

            int at = Mathf.Clamp(start + i, 0, m_sequence.Count - 1);
            (EAccessorySlot Slot, int Index) shown = m_sequence[at];
            Sprite icon = m_catalog.IconOf(shown.Slot, shown.Index);

            cell.Icon.sprite = icon;
            cell.Icon.enabled = icon != null;
            cell.Label.text = icon != null ? string.Empty : CosmeticNames.Of(m_catalog.Get(shown.Slot, shown.Index));
        }
    }

    private void ShowResult(EAccessorySlot slot, int index, bool gained)
    {
        if (m_resultLabel == null)
            return;

        LocalizedString format = gained ? m_resultFormat : m_duplicateFormat;
        format.Arguments = new object[] { CosmeticNames.Of(m_catalog.Get(slot, index)) };
        m_resultLabel.text = format.GetLocalizedString();
    }

    // 카탈로그의 클립·볼륨만 받아 내 소스로 튼다 — 배선 지점은 다른 소리와 같게 유지된다
    private void PlaySpinSound()
    {
        AudioLibrary.Entry entry = App.Sound?.GetSfxEntry(m_spinSound);
        if (m_spinSource == null || entry?.Clip == null)
            return;

        m_spinSource.clip = entry.Clip;
        m_spinSource.volume = entry.Volume;
        // 앞을 건너뛰는 배선도 따른다 — 풀로 낼 때와 같게 들려야 한다
        m_spinSource.time = Mathf.Clamp(entry.StartOffset, 0f, Mathf.Max(0f, entry.Clip.length - 0.05f));
        m_spinSource.Play();
    }

    private void StopSpinSound()
    {
        if (m_spinSource != null && m_spinSource.isPlaying)
            m_spinSource.Stop();
    }

    private void SetFrameColor(Color color)
    {
        if (m_frame == null)
            return;

        foreach (Graphic bar in m_frame)
            if (bar != null)
                bar.color = color;
    }

    private class Cell
    {
        public RectTransform Root;
        public Image Icon;
        public TextMeshProUGUI Label;
    }
}
