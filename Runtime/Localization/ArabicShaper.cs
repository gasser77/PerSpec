// ArabicShaper — pre-shape Arabic / Persian / Urdu canonical text into Unicode
// Presentation Forms-B (FB50-FBFF, FE70-FEFF). After shaping, each letter is in its
// final init/medi/fina/isol glyph form, so TMP / any renderer can display connected
// letters without an OpenType shaper or RTL flag.
//
// PERSPEC COPY — used by LocalizationTaskExecutor at scenario-execute time (AI-generated Arabic text,
// language-dropdown native names, anywhere Arabic comes from outside the localization
// table). LocalizationRules.md §13 mandates that strings stored in localization tables
// are ALREADY shaped at write-time via UnityHelper SetString shape="auto"; runtime
// never re-shapes localization values. This copy is for runtime-emitted (non-table)
// Arabic text only.
//
// Sync note: an identical copy lives in PerSpec/Runtime/Localization/ArabicShaper.cs
// for editor-time use by LocalizationTaskExecutor. If this file changes, update both.
//
// Coverage: 28 Arabic letters + hamza variants + alef madda/hamza pairs + Lam-Alif
// ligatures (FEF5–FEFC) + Persian extras (پ چ ژ ک گ ی) + Urdu extras (ٹ ڈ ڑ ں ہ ے).
// NOT covered: Tatweel-aware extension, complex Nastaliq ligatures, tashkeel
// reordering. Diacritics (U+064B-U+065F, U+0670) are passed through and treated as
// transparent for joining purposes.
//
// Algorithm — for each letter:
//   1. Look up joining type (right / dual / non-joining).
//   2. Look at left + right neighbors, skipping transparent (diacritic) chars.
//   3. Pick form: isolated / initial / medial / final.
//   4. Replace with Presentation Form-B codepoint.
//   5. After per-letter shaping, scan for LAM + ALEF and replace with one of FEF5-FEFC.

using System.Collections.Generic;
using System.Text;

namespace PerSpec.Runtime.Localization
{
    public static class ArabicShaper
    {
        private enum JoiningType { NonJoining, RightJoining, DualJoining }

        // Forms: [isolated, final, initial, medial]. RightJoining letters use only [0,1].
        private struct LetterForms
        {
            public JoiningType joining;
            public char isol, fina, init, medi;
        }

        // Diacritic (transparent) range — ignored when computing neighbor joins.
        private static bool IsTransparent(char c)
        {
            return (c >= 'ً' && c <= 'ٟ')
                || c == 'ٰ'           // ALEF SUPERSCRIPT
                || (c >= 'ۖ' && c <= 'ۭ');
        }

        private static readonly Dictionary<char, LetterForms> Table = BuildTable();

        // U+200A HAIR SPACE — inserted after letters that don't connect forward (right-joining
        // always; dual-joining only when chosen form is isolated or final). Visually breaks up
        // adjacent non-joining letters which Noto Sans otherwise packs too tightly.
        private const char HairSpace = '\u200A';

        public static string Shape(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            char[] chars = input.ToCharArray();
            char[] shaped = new char[chars.Length];
            // True when shaped[i]'s chosen form keeps the run open to the next letter (INIT/MEDI
            // for dual-joining). False for RightJoining and ISOL/FINAL of dual-joining.
            bool[] connectsFwd = new bool[chars.Length];

            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!Table.TryGetValue(c, out LetterForms forms))
                {
                    shaped[i] = c;
                    connectsFwd[i] = false;
                    continue;
                }

                bool prevJoinsForward = JoinsForward(chars, i, -1);
                bool nextJoinsBackward = JoinsBackward(chars, i, +1);

                if (forms.joining == JoiningType.RightJoining)
                {
                    shaped[i] = prevJoinsForward ? forms.fina : forms.isol;
                    connectsFwd[i] = false;
                }
                else if (forms.joining == JoiningType.DualJoining)
                {
                    bool joinPrev = prevJoinsForward;
                    bool joinNext = nextJoinsBackward;
                    if (joinPrev && joinNext)      { shaped[i] = forms.medi; connectsFwd[i] = true; }
                    else if (joinPrev)             { shaped[i] = forms.fina; connectsFwd[i] = false; }
                    else if (joinNext)             { shaped[i] = forms.init; connectsFwd[i] = true; }
                    else                           { shaped[i] = forms.isol; connectsFwd[i] = false; }
                }
                else
                {
                    shaped[i] = c;
                    connectsFwd[i] = false;
                }
            }

            return ApplyLamAlefAndSpacing(shaped, connectsFwd, chars);
        }

        private static bool JoinsForward(char[] s, int idx, int direction)
        {
            // Walk back across transparent (diacritic) chars to find the actual previous letter.
            int j = idx + direction;
            while (j >= 0 && j < s.Length && IsTransparent(s[j])) j += direction;
            if (j < 0 || j >= s.Length) return false;
            if (!Table.TryGetValue(s[j], out LetterForms f)) return false;
            return f.joining == JoiningType.DualJoining; // dual-joining letters extend forward
        }

        private static bool JoinsBackward(char[] s, int idx, int direction)
        {
            int j = idx + direction;
            while (j >= 0 && j < s.Length && IsTransparent(s[j])) j += direction;
            if (j < 0 || j >= s.Length) return false;
            if (!Table.TryGetValue(s[j], out LetterForms f)) return false;
            return f.joining == JoiningType.DualJoining || f.joining == JoiningType.RightJoining;
        }

        private static string ApplyLamAlefAndSpacing(char[] shaped, bool[] connectsFwd, char[] original)
        {
            var sb = new StringBuilder(shaped.Length + shaped.Length / 4);
            for (int i = 0; i < shaped.Length; i++)
            {
                bool emittedLamAlef = false;
                int consumedUpTo = i;

                if (i + 1 < shaped.Length && IsLamForm(shaped[i]))
                {
                    char alefOrig = original[i + 1];
                    if (TryGetLamAlef(alefOrig, isInitialOrMedialLam: shaped[i] == 'ﻟ' || shaped[i] == 'ﻠ',
                                       out char ligature))
                    {
                        sb.Append(ligature);
                        consumedUpTo = i + 1; // also consume ALEF
                        emittedLamAlef = true;
                    }
                }

                if (!emittedLamAlef)
                {
                    sb.Append(shaped[i]);
                }

                // Decide whether this position needs a trailing hair space. Position "outputs"
                // the rightmost char it consumed (consumedUpTo). The Lam-Alef ligature is itself
                // a non-forward-connecting glyph (alef is right-joining, terminates the run).
                bool currentConnectsFwd = !emittedLamAlef && connectsFwd[i];
                int nextIdx = consumedUpTo + 1;
                bool nextIsLetter = nextIdx < original.Length && Table.ContainsKey(original[nextIdx]);
                if (!currentConnectsFwd && nextIsLetter)
                {
                    sb.Append(HairSpace);
                }

                i = consumedUpTo;
            }
            return sb.ToString();
        }

        private static bool IsLamForm(char c) =>
            c == 'ﻝ' || c == 'ﻞ' || c == 'ﻟ' || c == 'ﻠ';

        // Map (LAM + alef-variant) → ligature. The "isInitialOrMedialLam" flag picks between
        // isolated (FEF5/FEF7/FEF9/FEFB) and final (FEF6/FEF8/FEFA/FEFC) ligature codepoints.
        private static bool TryGetLamAlef(char alef, bool isInitialOrMedialLam, out char ligature)
        {
            switch (alef)
            {
                case 'آ': ligature = isInitialOrMedialLam ? 'ﻵ' : 'ﻶ'; return true; // ALEF MADDA
                case 'أ': ligature = isInitialOrMedialLam ? 'ﻷ' : 'ﻸ'; return true; // ALEF HAMZA ABOVE
                case 'إ': ligature = isInitialOrMedialLam ? 'ﻹ' : 'ﻺ'; return true; // ALEF HAMZA BELOW
                case 'ا': ligature = isInitialOrMedialLam ? 'ﻻ' : 'ﻼ'; return true; // ALEF
            }
            ligature = '\0';
            return false;
        }

        private static Dictionary<char, LetterForms> BuildTable()
        {
            var t = new Dictionary<char, LetterForms>();

            // Helper: dual-joining (4 forms).
            void D(char c, char isol, char fina, char init, char medi)
                => t[c] = new LetterForms { joining = JoiningType.DualJoining, isol = isol, fina = fina, init = init, medi = medi };
            // Helper: right-joining (2 forms).
            void R(char c, char isol, char fina)
                => t[c] = new LetterForms { joining = JoiningType.RightJoining, isol = isol, fina = fina, init = isol, medi = fina };
            // Helper: non-joining (only isolated).
            void N(char c, char isol)
                => t[c] = new LetterForms { joining = JoiningType.NonJoining, isol = isol, fina = isol, init = isol, medi = isol };

            // ---- Arabic letters ----
            N('ء', 'ﺀ');                                    // HAMZA
            R('آ', 'ﺁ', 'ﺂ');                           // ALEF MADDA
            R('أ', 'ﺃ', 'ﺄ');                           // ALEF HAMZA ABOVE
            R('ؤ', 'ﺅ', 'ﺆ');                           // WAW HAMZA
            R('إ', 'ﺇ', 'ﺈ');                           // ALEF HAMZA BELOW
            D('ئ', 'ﺉ', 'ﺊ', 'ﺋ', 'ﺌ');       // YEH HAMZA
            R('ا', 'ﺍ', 'ﺎ');                           // ALEF
            D('ب', 'ﺏ', 'ﺐ', 'ﺑ', 'ﺒ');       // BEH
            R('ة', 'ﺓ', 'ﺔ');                           // TEH MARBUTA
            D('ت', 'ﺕ', 'ﺖ', 'ﺗ', 'ﺘ');       // TEH
            D('ث', 'ﺙ', 'ﺚ', 'ﺛ', 'ﺜ');       // THEH
            D('ج', 'ﺝ', 'ﺞ', 'ﺟ', 'ﺠ');       // JEEM
            D('ح', 'ﺡ', 'ﺢ', 'ﺣ', 'ﺤ');       // HAH
            D('خ', 'ﺥ', 'ﺦ', 'ﺧ', 'ﺨ');       // KHAH
            R('د', 'ﺩ', 'ﺪ');                           // DAL
            R('ذ', 'ﺫ', 'ﺬ');                           // THAL
            R('ر', 'ﺭ', 'ﺮ');                           // REH
            R('ز', 'ﺯ', 'ﺰ');                           // ZAIN
            D('س', 'ﺱ', 'ﺲ', 'ﺳ', 'ﺴ');       // SEEN
            D('ش', 'ﺵ', 'ﺶ', 'ﺷ', 'ﺸ');       // SHEEN
            D('ص', 'ﺹ', 'ﺺ', 'ﺻ', 'ﺼ');       // SAD
            D('ض', 'ﺽ', 'ﺾ', 'ﺿ', 'ﻀ');       // DAD
            D('ط', 'ﻁ', 'ﻂ', 'ﻃ', 'ﻄ');       // TAH
            D('ظ', 'ﻅ', 'ﻆ', 'ﻇ', 'ﻈ');       // ZAH
            D('ع', 'ﻉ', 'ﻊ', 'ﻋ', 'ﻌ');       // AIN
            D('غ', 'ﻍ', 'ﻎ', 'ﻏ', 'ﻐ');       // GHAIN
            D('ف', 'ﻑ', 'ﻒ', 'ﻓ', 'ﻔ');       // FEH
            D('ق', 'ﻕ', 'ﻖ', 'ﻗ', 'ﻘ');       // QAF
            D('ك', 'ﻙ', 'ﻚ', 'ﻛ', 'ﻜ');       // KAF
            D('ل', 'ﻝ', 'ﻞ', 'ﻟ', 'ﻠ');       // LAM
            D('م', 'ﻡ', 'ﻢ', 'ﻣ', 'ﻤ');       // MEEM
            D('ن', 'ﻥ', 'ﻦ', 'ﻧ', 'ﻨ');       // NOON
            D('ه', 'ﻩ', 'ﻪ', 'ﻫ', 'ﻬ');       // HEH
            R('و', 'ﻭ', 'ﻮ');                           // WAW
            R('ى', 'ﻯ', 'ﻰ');                           // ALEF MAKSURA
            D('ي', 'ﻱ', 'ﻲ', 'ﻳ', 'ﻴ');       // YEH

            // ---- Persian / Urdu extras (FB50-FBFF presentation block) ----
            D('پ', 'ﭖ', 'ﭗ', 'ﭘ', 'ﭙ');       // PEH
            D('چ', 'ﭺ', 'ﭻ', 'ﭼ', 'ﭽ');       // TCHEH
            R('ژ', 'ﮊ', 'ﮋ');                           // JEH
            D('ک', 'ﮎ', 'ﮏ', 'ﮐ', 'ﮑ');       // KEHEH (Persian KAF)
            D('گ', 'ﮒ', 'ﮓ', 'ﮔ', 'ﮕ');       // GAF
            D('ی', 'ﯼ', 'ﯽ', 'ﯾ', 'ﯿ');       // FARSI YEH
            D('ٹ', 'ﭦ', 'ﭧ', 'ﭨ', 'ﭩ');       // TTEH (Urdu)
            R('ڈ', 'ﮈ', 'ﮉ');                           // DDAL (Urdu)
            R('ڑ', 'ﮌ', 'ﮍ');                           // RREH (Urdu)
            D('ں', 'ﮞ', 'ﮟ', 'ﮞ', 'ﮟ');       // NOON GHUNNA (only isol+final)
            D('ہ', 'ﮦ', 'ﮧ', 'ﮨ', 'ﮩ');       // HEH GOAL
            R('ے', 'ﮮ', 'ﮯ');                           // YEH BARREE

            return t;
        }
    }
}
