using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CoursePilot
{
    public sealed class DeepSeekSettingsWindow : Window
    {
        public string ResultKey { get; private set; }
        private readonly PasswordBox password;
        private readonly CheckBox remember;
        private readonly TextBlock status;
        private readonly Button test, save, clear;
        private readonly DeepSeekKeyStore store;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();

        public DeepSeekSettingsWindow(Window owner, string key, DeepSeekKeyStore store)
        {
            if (owner.IsVisible) Owner = owner;
            this.store = store; ResultKey = key ?? "";
            Title = "随课 · DeepSeek 设置";
            Width = 540; Height = 510; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13; Background = new SolidColorBrush(Color.FromRgb(246, 246, 246));
            StackPanel panel = new StackPanel { Margin = new Thickness(28, 23, 28, 22) };
            panel.Children.Add(new TextBlock { Text = "DeepSeek", FontSize = 25, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = "每位使用者配置自己的 Key，用于章节测验解答与复核。", FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 9, 0, 23) });
            panel.Children.Add(new TextBlock { Text = "模型", Foreground = Brushes.Gray, FontSize = 11 });
            panel.Children.Add(new TextBox { Text = "DeepSeek V4.1 Flash  /  deepseek-flash", IsReadOnly = true, Background = Brushes.White, BorderBrush = Brushes.LightGray, Padding = new Thickness(10, 9, 10, 9), Margin = new Thickness(0, 7, 0, 18) });
            panel.Children.Add(new TextBlock { Text = "API Key", Foreground = Brushes.Gray, FontSize = 11 });
            password = new PasswordBox { Password = ResultKey, Padding = new Thickness(10, 9, 10, 9), Background = Brushes.White, BorderBrush = Brushes.LightGray, Margin = new Thickness(0, 7, 0, 12) };
            panel.Children.Add(password);
            remember = new CheckBox { Content = "在本机记住密钥（仅当前 Windows 用户）", IsChecked = System.IO.File.Exists(store.Path), Margin = new Thickness(0, 2, 0, 13) };
            panel.Children.Add(remember);
            panel.Children.Add(new TextBlock { Text = "密钥保存在本机用户目录，分发包不携带密钥。\n测验还需配置 GLM；题目必要截图发送至两家进行交叉审核。", FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
            status = new TextBlock { Text = WindowsQuizOcr.LanguageStatus(), FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, MinHeight = 38, Margin = new Thickness(0, 15, 0, 8) };
            panel.Children.Add(status);
            Grid buttons = new Grid();
            buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition());
            test = new Button { Content = "连接测试", Height = 38, Margin = new Thickness(0, 0, 8, 0), Style = (Style)owner.FindResource(typeof(Button)) };
            clear = new Button { Content = "清除密钥", Height = 38, Margin = new Thickness(0, 0, 8, 0), Style = (Style)owner.FindResource(typeof(Button)) };
            save = new Button { Content = "保存并返回", Height = 38, Style = (Style)owner.FindResource("Primary") };
            Grid.SetColumn(clear, 1); Grid.SetColumn(save, 2);
            buttons.Children.Add(test); buttons.Children.Add(clear); buttons.Children.Add(save); panel.Children.Add(buttons);
            Content = panel;
            test.Click += async delegate
            {
                test.IsEnabled = save.IsEnabled = clear.IsEnabled = false;
                status.Text = "正在验证 Key 与模型权限…";
                try
                {
                    using (DeepSeekClient client = new DeepSeekClient(password.Password, delegate(string message) { Dispatcher.Invoke(new Action(delegate { status.Text = message; })); }))
                        await client.TestConnection(lifetime.Token);
                    status.Text = "连接成功，deepseek-flash 可用。";
                }
                catch (OperationCanceledException) { }
                catch (QuizPauseException exception) { status.Text = exception.Message; }
                catch { status.Text = "连接测试未完成，请检查网络与 Key。"; }
                finally { test.IsEnabled = save.IsEnabled = clear.IsEnabled = true; }
            };
            save.Click += delegate
            {
                try
                {
                    string value = password.Password.Trim(); DeepSeekKeyStore.Validate(value);
                    if (remember.IsChecked == true) store.Save(value); else store.Clear();
                    ResultKey = value; DialogResult = true;
                }
                catch (QuizPauseException exception) { status.Text = exception.Message; }
                catch { status.Text = "无法保存本机密钥，请检查用户目录写入权限。"; }
            };
            clear.Click += delegate
            {
                try { store.Clear(); ResultKey = ""; password.Clear(); DialogResult = true; }
                catch { status.Text = "本机密钥未能清除，请检查用户目录权限。"; }
            };
            Closing += delegate { lifetime.Cancel(); };
        }
    }
}
