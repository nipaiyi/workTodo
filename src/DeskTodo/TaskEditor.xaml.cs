using System;
using System.Windows;
using DeskTodo.Core;

namespace DeskTodo;

public partial class TaskEditor : Window
{
    private readonly Action<string, string, DateOnly> save;
    public TaskEditor(TodoItem? item, string initialTitle, DateOnly initialDate, Action<string, string, DateOnly> save)
    {
        InitializeComponent();
        this.save = save;
        Heading.Text = item == null ? "添加任务" : "编辑任务";
        Title = Heading.Text;
        TitleInput.Text = item?.Title ?? initialTitle;
        NotesInput.Text = item?.Notes ?? "";
        DateInput.SelectedDate = (item?.PlannedDate ?? initialDate).ToDateTime(TimeOnly.MinValue);
        Loaded += (_, _) => { TitleInput.Focus(); TitleInput.CaretIndex = TitleInput.Text.Length; };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var title = TodoItem.ValidateTitle(TitleInput.Text);
            if (DateInput.SelectedDate is not { } date) throw new ArgumentException("请选择有效的计划日期。");
            save(title, NotesInput.Text, DateOnly.FromDateTime(date));
            DialogResult = true;
        }
        catch (Exception ex) { ErrorLabel.Text = "未保存：" + ex.Message; }
    }
}
