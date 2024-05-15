using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading.Tasks;
using ChatClient.Model;

namespace ChatClient.ViewModel
{
    internal class MainViewModel
    {
        public ObservableCollection<MessageModel> Messages { get; set; }
        private string _message;
        public string Message
        {
            get { return _message; }
            set { _message = value; }
        }
        public MainViewModel() 
        { 
            Messages = new ObservableCollection<MessageModel>();

        }
    }
}
