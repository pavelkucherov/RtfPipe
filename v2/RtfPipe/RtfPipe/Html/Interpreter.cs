using System.Collections.Generic;
using System.Text;
using System.Xml;
using RtfPipe.Tokens;
using System.Linq;

namespace RtfPipe
{
  internal class Interpreter
  {
    private readonly Stack<FormatContext> _inputStyle = new Stack<FormatContext>();
    private IHtmlWriter _html;
    private readonly XmlWriter _xml;

    public Interpreter(XmlWriter writer)
    {
      _xml = writer;
    }

    public void ToHtml(Document doc, RtfHtmlSettings settings)
    {
      _html = doc.HasHtml ? (IHtmlWriter)new DeencapsulationWriter(_xml) : new HtmlWriter(_xml, settings);

      var body = new Group();
      foreach (var token in doc.Contents)
      {
        if (token is DefaultFontRef defaultFont)
        {
          _html.DefaultFont = doc.FontTable.TryGetValue(defaultFont.Value, out var font) ? font : doc.FontTable.FirstOrDefault().Value;
        }
        else if (token is DefaultTabWidth tabWidth)
        {
          _html.DefaultTabWidth = tabWidth.Value;
        }
        else if (token is Group group)
        {
          if (group.Destination?.Type != TokenType.HeaderTag)
          {
            body.Contents.Add(token);
          }
        }
        else if (token.Type != TokenType.HeaderTag)
        {
          body.Contents.Add(token);
        }
      }

      ToHtmlGroup(doc, body, true);
      _html.Close();
    }

    private void ToHtmlGroup(Document doc, Group group, bool processRtf)
    {
      var tabCount = 0;

      if (group.Contents.Count > 1
        && group.Contents[0] is IgnoreUnrecognized
        && (group.Contents[1].GetType().Name == "GenericTag" || group.Contents[1].GetType().Name == "GenericWord"))
      {
        return;
      }

      if (_inputStyle.Count > 0)
        _inputStyle.Push(_inputStyle.Peek().Clone());
      else
        _inputStyle.Push(new FormatContext());
      var currStyle = _inputStyle.Peek();

      for (var i = 0; i < group.Contents.Count; i++)
      {
        var token = group.Contents[i];
        if (token is HtmlRtf htmlRtf)
        {
          processRtf = !htmlRtf.Value;
        }
        else if (processRtf)
        {
          if (token is RowDefaults && !(group is Row))
          {
            var table = Table.Create(group.Contents, ref i);
            foreach (var child in table.Contents.OfType<Group>())
              ToHtmlGroup(doc, child, processRtf);
          }
          else if (token.Type == TokenType.CellFormat)
          {
            var start = i;
            while (i < group.Contents.Count && !(group.Contents[i] is RightCellBoundary))
              i++;
            var cell = new CellToken(group.Contents.Skip(start).Take(i - start + 1)
              , group as Row
              , currStyle.OfType<CellToken>().LastOrDefault());
            currStyle.Add(cell);
          }
          else if (token is ControlWord<BorderPosition> borderSide)
          {
            var border = new BorderToken(borderSide);
            i++;
            while (i < group.Contents.Count && border.Add(group.Contents[i]))
              i++;
            i--;
            currStyle.Add(border);
          }
          else if ((token.Type & TokenType.Format) == TokenType.Format)
          {
            currStyle.Add(token);
          }
          else if (token is Group childGroup)
          {
            var dest = childGroup.Destination;
            if (dest is NumberingTextFallback
              || dest is ListTextFallback
              || dest?.Type == TokenType.HeaderTag)
            {
              // skip
            }
            else if (dest is FieldInstructions)
            {
              var instructions = childGroup.Contents
                .OfType<Group>().LastOrDefault(g => g.Destination == null && g.Contents.OfType<TextToken>().Any())
                ?.Contents.OfType<TextToken>().FirstOrDefault()?.Value?.Trim();
              if (string.IsNullOrEmpty(instructions)
                && !childGroup.Contents.OfType<Group>().Any()
                && childGroup.Contents.Count == 3)
              {
                instructions = (childGroup.Contents[2] as TextToken)?.Value;
              }

              if (!string.IsNullOrEmpty(instructions))
              {
                var args = instructions.Split(' ');
                if (args[0] == "HYPERLINK")
                  currStyle.Add(new HyperlinkToken(args));
              }
            }
            else if (dest is BookmarkStart)
            {
              currStyle.Add(new BookmarkToken()
              {
                Start = true,
                Id = childGroup.Contents.OfType<TextToken>().FirstOrDefault()?.Value
              });
            }
            else if (dest is BookmarkEnd)
            {
              currStyle.Add(new BookmarkToken()
              {
                Start = false,
                Id = childGroup.Contents.OfType<TextToken>().FirstOrDefault()?.Value
              });
            }
            else if (dest is PictureTag)
            {
              var pict = new Picture(childGroup);
              var style = FixStyles(doc, currStyle);
              if (tabCount > 0)
                _html.AddBreak(style, new Tab(), tabCount);
              _html.AddPicture(style, pict);
              tabCount = 0;
            }
            else if (childGroup.Contents.OfType<ParagraphNumbering>().Any())
            {
              foreach (var child in childGroup.Contents.Where(t => t.Type == TokenType.ParagraphFormat))
                currStyle.Add(child);
            }
            else
            {
              ToHtmlGroup(doc, childGroup, processRtf);
            }
          }
          else if (token is Tab)
          {
            tabCount++;
          }
          else if (token is TextToken text)
          {
            var style = FixStyles(doc, currStyle);
            if (tabCount > 0)
              _html.AddBreak(style, new Tab(), tabCount);
            _html.AddText(style, text.Value);
            tabCount = 0;
          }
          else if ((token.Type & TokenType.BreakTag) == TokenType.BreakTag)
          {
            _html.AddBreak(FixStyles(doc, currStyle), token);
            if (token is RowBreak)
            {
              foreach (var style in _inputStyle)
                style.InTable = false;
            }
            tabCount = 0;
          }
        }
        else if (token is Group childGroup2)
        {
          ToHtmlGroup(doc, childGroup2, processRtf);
        }
      }

      _inputStyle.Pop();
    }

    private static FormatContext FixStyles(Document doc, FormatContext style)
    {
      var styleId = style.RemoveFirstOfType<ListStyleId>();
      if (styleId != null && doc.ListStyles.TryGetValue(styleId.Value, out var listStyle))
      {
        var levelNum = style.RemoveFirstOfType<ListLevelNumber>() ?? new ListLevelNumber(0);
        var level = levelNum.Value;
        style.AddNew(listStyle.Style.Levels[level]
          .Where(t => t.Type == TokenType.ParagraphFormat
            && !(t is FirstLineIndent || t is LeftIndent)));
        style.Add(styleId);
        style.Add(levelNum);
      }
      return style;
    }
  }
}