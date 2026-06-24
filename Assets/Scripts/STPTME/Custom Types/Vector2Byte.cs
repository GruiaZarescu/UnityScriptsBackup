using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;


namespace CustomTypes
{

    public struct Vector2SByte : IEquatable<Vector2SByte>, IFormattable
    {
        private sbyte m_X;

        private sbyte m_Y;

        private static readonly Vector2SByte s_Zero = new Vector2SByte(0, 0);

        private static readonly Vector2SByte s_One = new Vector2SByte(1, 1);

        private static readonly Vector2SByte s_Up = new Vector2SByte(0, 1);

        private static readonly Vector2SByte s_Down = new Vector2SByte(0, -1);

        private static readonly Vector2SByte s_Left = new Vector2SByte(-1, 0);

        private static readonly Vector2SByte s_Right = new Vector2SByte(1, 0);

        public sbyte x
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return m_X;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                m_X = value;
            }
        }

        public sbyte y
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return m_Y;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                m_Y = value;
            }
        }

        public int this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return index switch
                {
                    0 => x,
                    1 => y,
                    _ => throw new IndexOutOfRangeException($"Invalid Vector2Byte index addressed: {index}!"),
                };
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                switch (index)
                {
                    case 0:
                        x = (sbyte)value;
                        break;
                    case 1:
                        y = (sbyte)value;
                        break;
                    default:
                        throw new IndexOutOfRangeException($"Invalid Vector2Byte index addressed: {index}!");
                }
            }
        }

        public float magnitude
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return Mathf.Sqrt(x * x + y * y);
            }
        }

        public int sqrMagnitude
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return x * x + y * y;
            }
        }

        public static Vector2SByte zero
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return s_Zero;
            }
        }

        public static Vector2SByte one
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return s_One;
            }
        }

        public static Vector2SByte up
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return s_Up;
            }
        }

        public static Vector2SByte down
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return s_Down;
            }
        }

        public static Vector2SByte left
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return s_Left;
            }
        }


        public static Vector2SByte right
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return s_Right;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2SByte(sbyte x, sbyte y)
        {
            m_X = x;
            m_Y = y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(sbyte x, sbyte y)
        {
            m_X = x;
            m_Y = y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Distance(Vector2SByte a, Vector2SByte b)
        {
            float num = a.x - b.x;
            float num2 = a.y - b.y;
            return (float)Math.Sqrt(num * num + num2 * num2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2SByte Min(Vector2SByte lhs, Vector2SByte rhs)
        {
            return new Vector2SByte((sbyte)Mathf.Min(lhs.x, rhs.x), (sbyte)Mathf.Min(lhs.y, rhs.y));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2SByte Max(Vector2SByte lhs, Vector2SByte rhs)
        {
            return new Vector2SByte((sbyte)Mathf.Max(lhs.x, rhs.x), (sbyte)Mathf.Max(lhs.y, rhs.y));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2SByte Scale(Vector2SByte a, Vector2SByte b)
        {
            return new Vector2SByte((sbyte)(a.x * b.x), (sbyte)(a.y * b.y));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Scale(Vector2SByte scale)
        {
            x *= scale.x;
            y *= scale.y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clamp(Vector2SByte min, Vector2SByte max)
        {
            x = Math.Max(min.x, x);
            x = Math.Min(max.x, x);
            y = Math.Max(min.y, y);
            y = Math.Min(max.y, y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator Vector2(Vector2SByte v)
        {
            return new Vector2(v.x, v.y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator Vector3Int(Vector2SByte v)
        {
            return new Vector3Int(v.x, v.y, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2SByte FloorToInt(Vector2 v)
        {
            return new Vector2SByte((sbyte)Mathf.FloorToInt(v.x), (sbyte)Mathf.FloorToInt(v.y));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2SByte CeilToInt(Vector2 v)
        {
            return new Vector2SByte((sbyte)Mathf.CeilToInt(v.x), (sbyte)Mathf.CeilToInt(v.y));
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2SByte RoundToInt(Vector2 v)
        {
            return new Vector2SByte((sbyte)Mathf.RoundToInt(v.x), (sbyte)Mathf.RoundToInt(v.y));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2SByte operator -(Vector2SByte v)
        {
            return new Vector2SByte((sbyte)-v.x, (sbyte)-v.y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2SByte operator +(Vector2SByte a, Vector2SByte b)
        {
            return new Vector2SByte((sbyte)(a.x + b.x), (sbyte)(a.y + b.y));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2SByte operator -(Vector2SByte a, Vector2SByte b)
        {
            return new Vector2SByte((sbyte)(a.x - b.x), (sbyte)(a.y - b.y));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2SByte operator *(Vector2SByte a, Vector2SByte b)
        {
            return new Vector2SByte((sbyte)(a.x * b.x), (sbyte)(a.y * b.y));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2SByte operator *(byte a, Vector2SByte b)
        {
            return new Vector2SByte((sbyte)(a * b.x), (sbyte)(a * b.y));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2SByte operator *(Vector2SByte a, int b)
        {
            return new Vector2SByte((sbyte)(a.x * b), (sbyte)(a.y * b));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2SByte operator /(Vector2SByte a, int b)
        {
            return new Vector2SByte((sbyte)(a.x / b), (sbyte)(a.y / b));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Vector2SByte lhs, Vector2SByte rhs)
        {
            return lhs.x == rhs.x && lhs.y == rhs.y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Vector2SByte lhs, Vector2SByte rhs)
        {
            return !(lhs == rhs);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool Equals(object other)
        {
            if (other is Vector2SByte other2)
            {
                return Equals(other2);
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(Vector2SByte other)
        {
            return x == other.x && y == other.y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode()
        {
            return (x * 73856093) ^ (y * 83492791);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override string ToString()
        {
            return ToString(null, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string ToString(string format)
        {
            return ToString(format, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string ToString(string format, IFormatProvider formatProvider)
        {
            if (formatProvider == null)
            {
                formatProvider = CultureInfo.InvariantCulture.NumberFormat;
            }

            return string.Format("({0}, {1})", x.ToString(format, formatProvider), y.ToString(format, formatProvider));
        }
    }
  
}