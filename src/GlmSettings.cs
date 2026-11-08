using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CoursePilot
{
    internal sealed class GlmSettingsStore
    {
        internal readonly DeepSeekKeyStore Key;
        private readonly string modelPath;
        internal GlmSettingsStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoursePilot")) { }
        internal GlmSettingsStore(string directory)
        { Key = new DeepSeekKeyStore(Path.Combine(directory, "glm.key")); modelPath = Path.Combine(directory, "glm-model.txt"); }
        internal string LoadModel()
        {
            string value = File.Exists(modelPath) ? File.ReadAllText(modelPath).Trim() : VisionModelClient.DefaultGlmModel;
            VisionModelClient.ValidateGlmModel(value); return value;
        }
        internal void SaveModel(string model)
        {
            VisionModelClient.ValidateGlmModel(model); Directory.CreateDirectory(Path.GetDirectoryName(modelPath));
            string temp = modelPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, model); if (File.Exists(modelPath)) File.Replace(temp, modelPath, null); else File.Move(temp, modelPath); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    internal sealed class GlmSettingsWindow : Window
    {
        internal string ResultKey { get; private set; }
        internal string ResultModel { get; private set; }
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        internal GlmSettingsWindow(Window owner, string key, string model, GlmSettingsStore store)
        {
            if (owner.IsVisible) Owner = owner;
            Title = "随课 · 智谱 GLM 设置"; Width = 550; Height = 550;
            ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13; Background = new SolidColorBrush(Color.FromRgb(246, 246, 246));
            ResultKey = key ?? ""; ResultModel = model;
            StackPanel panel = new StackPanel { Margin = new Thickness(28, 23, 28, 22) };
            panel.Children.Add(new TextBlock { Text = "智谱 GLM", FontSize = 25, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = "使用 bigmodel.cn 的 Key，与 DeepSeek 交叉审核。", Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 9, 0, 20) });
            panel.Children.Add(new TextBlock { Text = "视觉模型", Foreground = Brushes.Gray, FontSize = 11 });
            TextBox modelBox = new TextBox { Text = model, Background = Brushes.White, BorderBrush = Brushes.LightGray, Padding = new Thickness(10, 9, 10, 9), Margin = new Thickness(0, 7, 0, 18) };
            panel.Children.Add(modelBox);
            panel.Children.Add(new TextBlock { Text = "API Key", Foreground = Brushes.Gray, FontSize = 11 });
            PasswordBox password = new PasswordBox { Password = ResultKey, Background = Brushes.White, BorderBrush = Brushes.LightGray, Padding = new Thickness(10, 9, 10, 9), Margin = new Thickness(0, 7, 0, 12) };
            panel.Children.Add(password);
            CheckBox remember = new CheckBox { Content = "在本机记住密钥（仅当前 Windows 用户）", IsChecked = File.Exists(store.Key.Path), Margin = new Thickness(0, 2, 0, 13) };
            panel.Children.Add(remember);
            panel.Children.Add(new TextBlock { Text = "题目必要截图将发送至 DeepSeek 和智谱。密钥不随程序分发。\n连接测试会向所选模型发送内置测试图片，可能产生少量费用。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, LineHeight = 20 });
            TextBlock status = new TextBlock { Text = "默认 glm-4.6v；其他模型需具备图片输入权限。", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, FontSize = 11, MinHeight = 42, Margin = new Thickness(0, 15, 0, 10) };
            panel.Children.Add(status);
            Grid buttons = new Grid(); for (int i = 0; i < 3; i++) buttons.ColumnDefinitions.Add(new ColumnDefinition());
            Button test = new Button { Content = "连接测试", Height = 38, Margin = new Thickness(0, 0, 8, 0), Style = (Style)owner.FindResource(typeof(Button)) };
            Button clear = new Button { Content = "清除密钥", Height = 38, Margin = new Thickness(0, 0, 8, 0), Style = (Style)owner.FindResource(typeof(Button)) };
            Button save = new Button { Content = "保存并返回", Height = 38, Style = (Style)owner.FindResource("Primary") };
            Grid.SetColumn(clear, 1); Grid.SetColumn(save, 2); buttons.Children.Add(test); buttons.Children.Add(clear); buttons.Children.Add(save); panel.Children.Add(buttons);
            Content = panel;
            test.Click += async delegate
            {
                test.IsEnabled = clear.IsEnabled = save.IsEnabled = modelBox.IsEnabled = password.IsEnabled = false;
                status.Text = "正在测试所选视觉模型…";
                try
                {
                    using (VisionModelClient api = new VisionModelClient(true, password.Password, modelBox.Text.Trim(), delegate(string s) { Dispatcher.Invoke(new Action(delegate { status.Text = s; })); }))
                        await api.TestConnection(lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested(); status.Text = "连接成功，所选模型已接受图片请求。";
                }
                catch (OperationCanceledException) { }
                catch (QuizPauseException e) { status.Text = e.Message; }
                catch { status.Text = "连接失败，请检查网络、Key 和模型权限。"; }
                finally { test.IsEnabled = clear.IsEnabled = save.IsEnabled = modelBox.IsEnabled = password.IsEnabled = true; }
            };
            save.Click += delegate
            {
                try
                {
                    string value = password.Password.Trim(), selected = modelBox.Text.Trim();
                    VisionModelClient.ValidateKey(value, "GLM"); VisionModelClient.ValidateGlmModel(selected);
                    store.SaveModel(selected); if (remember.IsChecked == true) store.Key.Save(value); else store.Key.Clear();
                    ResultKey = value; ResultModel = selected; DialogResult = true;
                }
                catch (QuizPauseException e) { status.Text = e.Message; }
                catch { status.Text = "无法保存，请检查本机用户目录权限。"; }
            };
            clear.Click += delegate
            {
                try { store.Key.Clear(); ResultKey = ""; password.Clear(); DialogResult = true; }
                catch { status.Text = "清除失败，请检查本机用户目录权限。"; }
            };
            Closing += delegate { lifetime.Cancel(); };
        }
    }
}
