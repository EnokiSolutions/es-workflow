using System;
using System.Text;

namespace Es.Dpo
{
    public static class UrlEx
    {
        public static string After(this string x, string what)
        {
            var i = x.IndexOf(what, StringComparison.Ordinal);
            return i < 0 ? x : x.Substring(i+what.Length);
        }
        public static string Before(this string x, string what)
        {
            var i = x.IndexOf(what, StringComparison.Ordinal);
            return i < 0 ? x : x.Substring(0,i);
        }
        public static string UrlDecode(this string what)
        {
            var whatChars = what.ToCharArray();
            var chars = new char[whatChars.Length]; // might be smaller

            var o = 0;
            for (var i = 0; i < whatChars.Length; ++i)
            {
                var whatByte = whatChars[i];
                if (whatByte != '%')
                {
                    chars[o++] = whatByte;
                }
                else
                {
                    var highChar = (int) whatChars[++i];
                    var low = (int) whatChars[++i];
                    var highValue = highChar - (highChar < 58 ? 48 : (highChar < 97 ? 55 : 87));
                    var lowValue = low - (low < 58 ? 48 : (low < 97 ? 55 : 87));

                    if (highValue < 0 || highValue > 15 || lowValue < 0 || lowValue > 15)
                        throw new FormatException("urlDecode");

                    chars[o++] = (char) ((highValue << 4) + lowValue);
                }
            }

            return new string(chars);
        }

        public static string UrlEncode(this string what)
        {
            var sb = new StringBuilder();
            foreach (var b in what.ToCharArray())
            {
                sb.Append(
                    b <= ' ' || (b >= '[' && b <= '`') || (b >= 'z')
                        ? "%" + ((ushort) b).ToString("x2")
                        : b.ToString()
                    );
            }
            return sb.ToString();
        }
    }
}