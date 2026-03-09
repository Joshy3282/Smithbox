using Andre.Formats;
using StudioCore.Application;
using StudioCore.Editors.Common;
using System.Collections.Generic;
using System.Linq;

namespace StudioCore.Editors.ParamEditor;

public class ParamSelection
{
    public ParamEditorScreen Editor;
    public ProjectEntry Project;

    private static string _globalRowSearchString = "";
    private static string _globalPropSearchString = "";
    private readonly Dictionary<string, ParamSelectionState> _paramStates = new();

    private readonly List<string> _history = new();
    private int _historyIndex = -1;

    private string _activeParam;

    public ParamSelection(ParamEditorScreen editor, ProjectEntry project)
    {
        Editor = editor;
        Project = project;
    }

    private void PushHistory(string newParam)
    {
        if (_historyIndex >= 0 && _history[_historyIndex] == newParam)
            return;

        // Remove everything after current index
        if (_historyIndex < _history.Count - 1)
        {
            _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1));
        }

        _history.Add(newParam);
        _historyIndex++;

        if (_history.Count >= 100)
        {
            _history.RemoveAt(0);
            _historyIndex--;
        }
    }

    public void PopHistory()
    {
        GoBack();
    }

    public void GoBack()
    {
        if (_historyIndex > 0)
        {
            _historyIndex--;
            SetActiveParam(_history[_historyIndex], true);
        }
    }

    public void GoForward()
    {
        if (_historyIndex < _history.Count - 1)
        {
            _historyIndex++;
            SetActiveParam(_history[_historyIndex], true);
        }
    }

    public bool HasHistory()
    {
        return _historyIndex > 0;
    }

    public bool ActiveParamExists()
    {
        return _activeParam != null;
    }

    public string GetActiveParam()
    {
        return _activeParam;
    }

    public void SetActiveParam(string param, bool isHistory = false)
    {
        if (!isHistory)
        {
            PushHistory(param);
        }

        _activeParam = param;

        if (!_paramStates.ContainsKey(_activeParam))
        {
            _paramStates.Add(_activeParam, new ParamSelectionState());
        }
    }

    public ref string GetCurrentRowSearchString()
    {
        if (_activeParam == null)
        {
            return ref _globalRowSearchString;
        }

        return ref _paramStates[_activeParam].currentRowSearchString;
    }

    public ref string GetCurrentPropSearchString()
    {
        if (_activeParam == null)
        {
            return ref _globalPropSearchString;
        }

        return ref _paramStates[_activeParam].currentPropSearchString;
    }

    public void SetCurrentRowSearchString(string s)
    {
        if (_activeParam == null)
        {
            return;
        }

        _paramStates[_activeParam].currentRowSearchString = s;
        _paramStates[_activeParam].selectionCacheDirty = true;
    }

    public void SetCurrentPropSearchString(string s)
    {
        if (_activeParam == null)
        {
            return;
        }

        _paramStates[_activeParam].currentPropSearchString = s;
    }

    public bool RowSelectionExists()
    {
        return _activeParam != null && _paramStates[_activeParam].selectionRows.Count > 0;
    }

    public Param.Row GetActiveRow()
    {
        if (_activeParam == null)
        {
            return null;
        }

        return _paramStates[_activeParam].activeRow;
    }

    public Param.Row GetCompareRow()
    {
        if (_activeParam == null)
        {
            return null;
        }

        return _paramStates[_activeParam].compareRow;
    }

    public Param.Column GetCompareCol()
    {
        if (_activeParam == null)
        {
            return null;
        }

        return _paramStates[_activeParam].compareCol;
    }

    public void SetActiveRow(Param.Row row, bool clearSelection, bool isHistory = false)
    {
        if (_activeParam != null)
        {
            ParamSelectionState s = _paramStates[_activeParam];

            if (s.activeRow != null)
            {
                Editor.Project.Handler.ParamData.PrimaryBank.RefreshParamRowDiffs(Editor, s.activeRow, _activeParam);
            }

            s.activeRow = row;
            s.selectionRows.Clear();
            s.selectionRows.Add(row);
            if (s.activeRow != null)
                Editor.Project.Handler.ParamData.PrimaryBank.RefreshParamRowDiffs(Editor, s.activeRow, _activeParam);

            s.selectionCacheDirty = true;

            // WIP: add icon clear for future icon support here
        }
    }

    public bool IsDirty()
    {
        ParamSelectionState s = _paramStates[_activeParam];

        return s.selectionCacheDirty;
    }

    public void SetCompareRow(Param.Row row)
    {
        if (_activeParam != null)
        {
            ParamSelectionState s = _paramStates[_activeParam];
            s.compareRow = row;
        }
    }

    public void SetCompareCol(Param.Column col)
    {
        if (_activeParam != null)
        {
            ParamSelectionState s = _paramStates[_activeParam];
            s.compareCol = col;
        }
    }

    public void ToggleRowInSelection(Param.Row row)
    {
        if (_activeParam != null)
        {
            ParamSelectionState s = _paramStates[_activeParam];

            if (s.selectionRows.Contains(row))
            {
                s.selectionRows.Remove(row);
            }
            else
            {
                s.selectionRows.Add(row);
            }

            s.selectionCacheDirty = true;
        }
        //Do not perform vanilla diff here, will be very slow when making large selections
    }

    public void ClearRowSelection()
    {
        if (_activeParam != null)
        {
            ParamSelectionState s = _paramStates[_activeParam];

            s.selectionRows.Clear();
            s.selectionCacheDirty = true;
        }
    }

    public void AddRowToSelection(Param.Row row)
    {
        if (_activeParam != null)
        {
            ParamSelectionState s = _paramStates[_activeParam];

            if (!s.selectionRows.Contains(row))
            {
                s.selectionRows.Add(row);
                s.selectionCacheDirty = true;
            }
        }
        //Do not perform vanilla diff here, will be very slow when making large selections
    }

    public void RemoveRowFromSelection(Param.Row row)
    {
        if (_activeParam != null)
        {
            _paramStates[_activeParam].selectionRows.Remove(row);
            _paramStates[_activeParam].selectionCacheDirty = true;
        }
    }

    public void RemoveRowFromAllSelections(Param.Row row)
    {
        foreach (ParamSelectionState state in _paramStates.Values)
        {
            state.selectionRows.Remove(row);

            if (state.activeRow == row)
            {
                state.activeRow = null;
            }

            state.selectionCacheDirty = true;
        }
    }

    public List<Param.Row> GetSelectedRows()
    {
        if (_activeParam == null)
        {
            return null;
        }

        return _paramStates[_activeParam].selectionRows;
    }

    public bool[] GetSelectionCache(List<Param.Row> rows, string cacheVer)
    {
        if (_activeParam == null)
        {
            return null;
        }

        ParamSelectionState s = _paramStates[_activeParam];
        // We maintain this flag as clearing the cache properly is slow for the number of times we modify selection
        if (s.selectionCacheDirty)
        {
            CacheBank.RemoveCache(Editor, s);
        }

        return CacheBank.GetCached(Editor, s, "selectionCache" + cacheVer, () =>
        {
            s.selectionCacheDirty = false;
            return rows.Select(x => GetSelectedRows().Contains(x)).ToArray();
        });
    }

    public void CleanSelectedRows()
    {
        if (_activeParam != null)
        {
            ParamSelectionState s = _paramStates[_activeParam];
            s.selectionRows.Clear();
            if (s.activeRow != null)
            {
                s.selectionRows.Add(s.activeRow);
            }

            s.selectionCacheDirty = true;
        }
    }

    public void CleanAllSelectionState()
    {
        foreach (ParamSelectionState s in _paramStates.Values)
        {
            s.selectionCacheDirty = true;
        }

        _activeParam = null;
        _paramStates.Clear();
    }

    public void SortSelection()
    {
        if (_activeParam != null)
        {
            ParamSelectionState s = _paramStates[_activeParam];
            Param p = Editor.Project.Handler.ParamData.PrimaryBank.Params[_activeParam];
            s.selectionRows.Sort((a, b) => { return p.IndexOfRow(a) - p.IndexOfRow(b); });
        }
    }
}

internal class ParamSelectionState
{
    internal Param.Row activeRow;
    internal Param.Column compareCol;
    internal Param.Row compareRow;
    internal string currentPropSearchString = "";
    internal string currentRowSearchString = "";
    internal bool selectionCacheDirty = true;

    internal List<Param.Row> selectionRows = new();
}
