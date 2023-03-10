package com.example.makore;

import android.content.Intent;
import android.content.res.Configuration;
import android.os.Bundle;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;

import androidx.annotation.NonNull;
import androidx.fragment.app.Fragment;
import androidx.lifecycle.ViewModelProvider;
import androidx.recyclerview.widget.RecyclerView;

import com.example.makore.adapters.MessageListAdapter;
import com.example.makore.api.ContactAPI;
import com.example.makore.databinding.FragmentChatBinding;
import com.example.makore.entities.Message;
import com.example.makore.viewmodels.ContactsViewModel;

import java.time.LocalDateTime;
import java.time.format.DateTimeFormatter;
import java.util.Locale;

import retrofit2.Call;
import retrofit2.Callback;
import retrofit2.Response;

public class ChatFragment extends Fragment {

    private FragmentChatBinding binding;
    // private ContactsListAdapter adapter;
    private ContactsViewModel viewModel;
    private MessageListAdapter adapter;
    private String contactId;

    @Override
    public View onCreateView(
            @NonNull LayoutInflater inflater, ViewGroup container,
            Bundle savedInstanceState
    ) {

        binding = FragmentChatBinding.inflate(inflater, container, false);
        return binding.getRoot();

    }

    public void onViewCreated(@NonNull View view, Bundle savedInstanceState) {
        super.onViewCreated(view, savedInstanceState);
        viewModel = new ViewModelProvider(requireActivity()).get(ContactsViewModel.class);
        viewModel.reload();
        contactId = null;
        viewModel.getContactIdLiveData().observe(getViewLifecycleOwner(), id -> {
            contactId = id;
            if (contactId != null) {
                // Set contact name
                viewModel.setContactId(contactId);
                RecyclerView messagesRecyclerView = binding.lstMessages;
                adapter = new MessageListAdapter(getContext());
                messagesRecyclerView.setAdapter(adapter);
                messagesRecyclerView.setLayoutManager(new androidx.recyclerview.widget.LinearLayoutManager(getContext()));

                viewModel.getMessages().observe(getViewLifecycleOwner(), messages -> {
                    adapter.setMessages(viewModel.getMessagesWithContact());
                    System.out.println("Messages: " + messages);
                });
            }
        });

        if (contactId != null) {
            RecyclerView messagesRecyclerView = binding.lstMessages;
            adapter = new MessageListAdapter(getContext());
            messagesRecyclerView.setAdapter(adapter);
            messagesRecyclerView.setLayoutManager(new androidx.recyclerview.widget.LinearLayoutManager(getContext()));

            viewModel.getMessages().observe(getViewLifecycleOwner(), messages -> {
                adapter.setMessages(viewModel.getMessagesWithContact());
                System.out.println("Messages: " + messages);
            });
        }
        // Set contact name
        viewModel.setContactId(contactId);

        binding.sendBtn.setOnClickListener(v -> {
            if (contactId == null) {
                return;
            }
            String messageContent = binding.messageInput.getText().toString();
            if (messageContent.length() > 0) {
                // Format timestamp in MM/dd/yyyy HH:mm:ss
                String timestamp = null;
                if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.O) {
                    timestamp = LocalDateTime.now().format(DateTimeFormatter.ofPattern("MM/dd/yyyy, HH:mm:ss", Locale.US));
                }
                // Create new message object
                Message newMessage = new Message(messageContent, timestamp, true, contactId);
                // Insert message into database
                viewModel.insertMessage(newMessage);
                new ContactAPI()
                        .addMessage(newMessage.getContactId(), newMessage.getContent())
                        .enqueue(new Callback<>() {
                            @Override
                            public void onResponse(@NonNull Call<Void> call, @NonNull Response<Void> response) {
                            }

                            @Override
                            public void onFailure(@NonNull Call<Void> call, @NonNull Throwable t) {
                                t.printStackTrace();
                            }
                        });
                binding.messageInput.setText("");
            }
        });
    }

    @Override
    public void onDestroyView() {
        super.onDestroyView();
        binding = null;
    }

    @Override
    public void onConfigurationChanged(@NonNull Configuration newConfig) {
        super.onConfigurationChanged(newConfig);
        //Return to main activity
        Intent intent = new Intent(getContext(), MainActivity.class);
        startActivity(intent);
    }
}