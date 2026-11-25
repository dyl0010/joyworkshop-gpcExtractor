using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Text.RegularExpressions;

namespace GpcSessionBrowser;

public sealed class SearchResultTextBlock : TextBlock
{
    public SearchResultTextBlock()
    {
        TextWrapping = TextWrapping.Wrap;
        DataContextChanged += (_, _) => RebuildInlines();
    }

    private void RebuildInlines()
    {
        Inlines.Clear();
        string text;
        string query;
        if (DataContext is PromptSearchResult result)
        {
            text = result.DisplayName;
            query = result.SearchQuery.Trim();
        }
        else if (DataContext is PromptRecord prompt)
        {
            text = prompt.Preview;
            query = prompt.SearchQuery.Trim();
        }
        else
        {
            return;
        }
        if (query.Length == 0)
        {
            Inlines.Add(new Run(text));
            return;
        }

        var terms = Regex.Split(query, @"\s+");
        var searchStart = 0;
        while (searchStart < text.Length)
        {
            var matchIndex = -1;
            var matchLength = 0;
            foreach (var term in terms)
            {
                if (term.Length == 0)
                {
                    continue;
                }

                var candidateIndex = text.IndexOf(term, searchStart, StringComparison.OrdinalIgnoreCase);
                if (candidateIndex >= 0 && (matchIndex < 0 || candidateIndex < matchIndex))
                {
                    matchIndex = candidateIndex;
                    matchLength = term.Length;
                }
            }

            if (matchIndex < 0)
            {
                Inlines.Add(new Run(text[searchStart..]));
                break;
            }

            if (matchIndex > searchStart)
            {
                Inlines.Add(new Run(text[searchStart..matchIndex]));
            }

            Inlines.Add(new Run(text.Substring(matchIndex, matchLength))
            {
                Foreground = Brushes.Firebrick,
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(Color.FromRgb(254, 226, 226))
            });
            searchStart = matchIndex + matchLength;
        }
    }
}
