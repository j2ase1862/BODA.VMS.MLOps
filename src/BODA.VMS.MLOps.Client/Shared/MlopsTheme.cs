using MudBlazor;

namespace BODA.VMS.MLOps.Client.Shared;

/// <summary>
/// 화면 전체의 색·서체·모서리.
///
/// <para>
/// 산업용 비전 솔루션 웹(세이지 SAIGE VISION 등)의 화면 언어를 참고했다.
/// 그쪽의 공통점은 셋이다 — 흰 바탕에 거의 검은 글자로 대비를 세게 두고,
/// 강조색은 딱 하나만 아주 적게 쓰며, 모서리를 둥글리지 않아 화면이 도구처럼 보이게 한다.
/// 검사 화면은 사진과 라벨이 주인공이라 UI 가 색을 가져가면 안 된다는 점에서 이 방향이 맞다.
/// </para>
/// <para>
/// 그래서 MudBlazor 기본 보라색 대신 다음을 쓴다.
/// 강조색 하나(<see cref="Accent"/>)는 지금 누를 것과 지금 켜져 있는 것에만 쓰고,
/// 나머지는 검정·회색·흰색으로 끝낸다. 라벨 색(클래스 팔레트)과 부딪히지 않게 하려는 뜻도 있다.
/// </para>
/// </summary>
public static class MlopsTheme
{
    /// <summary>강조색. 지금 누를 것·켜져 있는 것에만 쓴다.</summary>
    public const string Accent = "#FF4052";

    /// <summary>본문 글자. 순검정보다 조금 눕힌 값이라 긴 표를 봐도 눈이 덜 아프다.</summary>
    public const string Ink = "#101010";

    public const string InkMuted = "#828280";
    public const string InkFaint = "#C1C1C1";
    public const string Line = "#DDDDDD";
    public const string Surface = "#FFFFFF";
    public const string Ground = "#F7F7F7";

    /// <summary>한글이 섞인 화면이라 맑은 고딕 계열을 앞에 둔다.</summary>
    private static readonly string[] Fonts =
        ["Pretendard", "Pretendard Variable", "Segoe UI", "Malgun Gothic", "system-ui", "sans-serif"];

    public static MudTheme Build() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = Accent,
            PrimaryContrastText = "#FFFFFF",
            Secondary = Ink,
            Tertiary = "#4169E1",

            Background = Ground,
            Surface = Surface,
            DrawerBackground = Surface,
            DrawerText = Ink,
            DrawerIcon = InkMuted,
            AppbarBackground = Surface,
            AppbarText = Ink,

            TextPrimary = Ink,
            TextSecondary = InkMuted,
            TextDisabled = InkFaint,
            ActionDefault = InkMuted,
            ActionDisabled = InkFaint,

            Divider = Line,
            DividerLight = "#EFEFEF",
            LinesDefault = Line,
            LinesInputs = Line,
            TableLines = "#EFEFEF",
            TableStriped = "#FAFAFA",
            TableHover = "#F5F5F5",

            // 상태색은 뜻이 분명한 것만 남긴다. 성공은 초록이되 튀지 않게, 오류는 강조색과 같은 계열.
            Success = "#1B8A5A",
            Warning = "#B26A00",
            Error = "#D32036",
            Info = "#4169E1",

            GrayDefault = InkMuted,
            GrayLight = "#EFEFEF",
            GrayLighter = "#F7F7F7",
        },

        LayoutProperties = new LayoutProperties
        {
            // 모서리를 죽인다. 검사 도구 화면은 둥글수록 장난감처럼 보인다.
            DefaultBorderRadius = "2px",
            DrawerWidthLeft = "232px",
            AppbarHeight = "56px",
        },

        Typography = new Typography
        {
            Default = new Default { FontFamily = Fonts, FontSize = "0.875rem", LineHeight = 1.6 },
            H1 = new H1 { FontFamily = Fonts, FontSize = "2rem", FontWeight = 700, LineHeight = 1.25 },
            H2 = new H2 { FontFamily = Fonts, FontSize = "1.625rem", FontWeight = 700, LineHeight = 1.3 },
            H3 = new H3 { FontFamily = Fonts, FontSize = "1.375rem", FontWeight = 700, LineHeight = 1.35 },
            H4 = new H4 { FontFamily = Fonts, FontSize = "1.25rem", FontWeight = 700, LineHeight = 1.4 },
            H5 = new H5 { FontFamily = Fonts, FontSize = "1.125rem", FontWeight = 700, LineHeight = 1.4 },
            H6 = new H6 { FontFamily = Fonts, FontSize = "1rem", FontWeight = 700, LineHeight = 1.45 },
            Subtitle1 = new Subtitle1 { FontFamily = Fonts, FontSize = "0.9375rem", FontWeight = 600 },
            Subtitle2 = new Subtitle2 { FontFamily = Fonts, FontSize = "0.8125rem", FontWeight = 700 },
            Body1 = new Body1 { FontFamily = Fonts, FontSize = "0.875rem", LineHeight = 1.6 },
            Body2 = new Body2 { FontFamily = Fonts, FontSize = "0.8125rem", LineHeight = 1.55 },
            Button = new Button { FontFamily = Fonts, FontSize = "0.8125rem", FontWeight = 600, TextTransform = "none" },
            Caption = new Caption { FontFamily = Fonts, FontSize = "0.75rem", LineHeight = 1.5 },
            Overline = new Overline { FontFamily = Fonts, FontSize = "0.6875rem", FontWeight = 700, LetterSpacing = "0.08em" },
        },
    };
}
