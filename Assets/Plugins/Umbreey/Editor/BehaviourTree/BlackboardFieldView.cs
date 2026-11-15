using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

public class BlackboardFieldView : UnityEditor.Experimental.GraphView.BlackboardField
{
    private BlackboardView _blackboardView;
    public BlackboardFieldView(BlackboardView view) : base()
    {
        _blackboardView = view;
    }

    protected override void BuildFieldContextualMenu(ContextualMenuPopulateEvent evt)
    {
        evt.menu.AppendAction("重命名", 
            (a => this.OpenTextEditor()), 
            DropdownMenuAction.AlwaysEnabled);
        
        evt.menu.AppendAction("删除", 
            (a) => _blackboardView.OnRemoveClicked(this),
            DropdownMenuAction.AlwaysEnabled);

        evt.StopPropagation();
    }
}
