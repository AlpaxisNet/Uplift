using System.Collections;
using System.Runtime.CompilerServices;

namespace Uplift.Syntax;

/// <summary>Whether two trees are the same: the same node types with the same values, lists compared item by item.</summary>
public static class SyntaxEquality
{
	public static bool Equal(object? a, object? b)
	{
		if (ReferenceEquals(a, b)) return true;
		if (a == null || b == null) return false;
		if (a.GetType() != b.GetType()) return false;
		switch (a)
		{
			case string s:
				return string.Equals(s, (string)b, StringComparison.Ordinal);
			case bool or int or double or char:
				return a.Equals(b);
			case IList list:
			{
				var other = (IList)b;
				if (list.Count != other.Count) return false;
				for (var i = 0; i < list.Count; i++)
					if (!Equal(list[i], other[i]))
						return false;
				return true;
			}
			case ITuple tuple:
			{
				var other = (ITuple)b;
				for (var i = 0; i < tuple.Length; i++)
					if (!Equal(tuple[i], other[i]))
						return false;
				return true;
			}
		}
		foreach (var property in a.GetType().GetProperties())
		{
			if (property.Name == "EqualityContract" || property.GetIndexParameters().Length > 0) continue;
			if (!Equal(property.GetValue(a), property.GetValue(b))) return false;
		}
		return true;
	}
}
