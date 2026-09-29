/*
 * Switcheroo - The incremental-search task switcher for Windows.
 * http://www.switcheroo.io/
 * Copyright 2009, 2010 James Sulak
 * Copyright 2014 Regin Larsen
 * 
 * Switcheroo is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * Switcheroo is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with Switcheroo.  If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml;
using Switcheroo.Core.Matchers;

namespace Switcheroo.Core
{
    public class XamlHighlighter
    {
        public string Highlight(IEnumerable<StringPart> stringParts)
        {
            if (stringParts == null) return string.Empty;

            // 直接用 StringBuilder 拼接并做 XML 转义，替代 XDocument 解析（每次高亮耗时从毫秒级降到近零）。
            var builder = new StringBuilder();
            foreach (var stringPart in stringParts)
            {
                var escaped = SecurityElement.Escape(stringPart.Value) ?? string.Empty;
                if (stringPart.IsMatch)
                {
                    builder.Append("<Bold>");
                    builder.Append(escaped);
                    builder.Append("</Bold>");
                }
                else
                {
                    builder.Append(escaped);
                }
            }
            return builder.ToString();
        }
    }
}